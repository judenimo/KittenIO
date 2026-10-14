"""Experimental launch guidance built on canonical telemetry and leased control.

The controller uses osculating two-body elements derived from CCI position and
velocity. It does not interpret the game's ambiguous apoapsis/periapsis fields.
Offline trajectory tests are not evidence of a live KSA flight.
"""

from __future__ import annotations

import math
import time
from dataclasses import dataclass
from typing import TYPE_CHECKING, Callable

from ..research.transforms import cross, dot, norm, normalize_vector, rotate_vector
from ..wire.common import Vector3
from ..wire.models import FlightState, UnsupportedCapabilityError

if TYPE_CHECKING:
    from ..runtime.engine import KsaApi


class OrbitLaunchError(RuntimeError):
    """Launch telemetry, authority, or the configured flight envelope is invalid."""


@dataclass(frozen=True, slots=True)
class OrbitLaunchSettings:
    target_altitude_m: float = 200_000.0
    heading_deg: float = 90.0
    turn_start_height_m: float = 500.0
    turn_end_height_m: float = 80_000.0
    maximum_acceleration_mps2: float = 30.0
    orbit_tolerance_m: float = 5_000.0
    maximum_sim_seconds: float = 5_400.0
    maximum_real_seconds: float = 7_200.0
    telemetry_timeout_s: float = 3.0
    launch_direction_cci: Vector3 | None = None

    def __post_init__(self) -> None:
        for name in (
            "target_altitude_m",
            "turn_start_height_m",
            "turn_end_height_m",
            "maximum_acceleration_mps2",
            "orbit_tolerance_m",
            "maximum_sim_seconds",
            "maximum_real_seconds",
            "telemetry_timeout_s",
        ):
            value = getattr(self, name)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
                raise ValueError(f"{name} must be finite and positive")
        if isinstance(self.heading_deg, bool) or not isinstance(self.heading_deg, (int, float)):
            raise TypeError("heading_deg must be numeric")
        if not math.isfinite(self.heading_deg) or not 0 <= self.heading_deg < 360:
            raise ValueError("heading_deg must be in [0, 360)")
        if self.turn_end_height_m <= self.turn_start_height_m:
            raise ValueError("turn_end_height_m must exceed turn_start_height_m")
        if self.turn_end_height_m >= self.target_altitude_m:
            raise ValueError("turn_end_height_m must be below target_altitude_m")
        if self.orbit_tolerance_m >= self.target_altitude_m:
            raise ValueError("orbit_tolerance_m must be smaller than target_altitude_m")
        if self.launch_direction_cci is not None:
            if not isinstance(self.launch_direction_cci, Vector3):
                raise TypeError("launch_direction_cci must be Vector3")
            normalize_vector(self.launch_direction_cci)


@dataclass(frozen=True, slots=True)
class OrbitInsertionResult:
    vehicle_id: str
    body_id: str
    apoapsis_altitude_m: float
    periapsis_altitude_m: float
    elapsed_sim_seconds: float


@dataclass(frozen=True, slots=True)
class _Orbit:
    radius_m: float
    radial_speed_mps: float
    tangential_speed_mps: float
    apoapsis_radius_m: float
    periapsis_radius_m: float
    seconds_to_apoapsis: float


def _scale(vector: Vector3, factor: float) -> Vector3:
    return Vector3(vector.x * factor, vector.y * factor, vector.z * factor)


def _add(left: Vector3, right: Vector3) -> Vector3:
    return Vector3(left.x + right.x, left.y + right.y, left.z + right.z)


def _clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def _orbit(position: Vector3, velocity: Vector3, mu: float) -> _Orbit:
    if not math.isfinite(mu) or mu <= 0:
        raise OrbitLaunchError("parent gravitational parameter must be finite and positive")
    radius = norm(position)
    if not math.isfinite(radius) or radius <= 0:
        raise OrbitLaunchError("vehicle position must be a finite nonzero CCI vector")
    speed_squared = dot(velocity, velocity)
    energy = speed_squared / 2 - mu / radius
    if not math.isfinite(energy) or energy >= 0:
        raise OrbitLaunchError("an unbound trajectory is outside this launch controller's envelope")
    angular_momentum = norm(cross(position, velocity))
    major_axis = -mu / (2 * energy)
    eccentricity = math.sqrt(max(0.0, 1 + 2 * energy * angular_momentum**2 / mu**2))
    radial_speed = dot(position, velocity) / radius
    if eccentricity < 1e-8:
        to_apoapsis = 0.0
    else:
        cos_e = _clamp((1 - radius / major_axis) / eccentricity, -1.0, 1.0)
        sin_e = dot(position, velocity) / (eccentricity * math.sqrt(mu * major_axis))
        eccentric_anomaly = math.atan2(sin_e, cos_e) % math.tau
        mean_anomaly = eccentric_anomaly - eccentricity * math.sin(eccentric_anomaly)
        to_apoapsis = ((math.pi - mean_anomaly) % math.tau) / math.sqrt(mu / major_axis**3)
    return _Orbit(
        radius,
        radial_speed,
        angular_momentum / radius,
        major_axis * (1 + eccentricity),
        major_axis * (1 - eccentricity),
        to_apoapsis,
    )


@dataclass(frozen=True, slots=True)
class _Command:
    direction: Vector3
    throttle: float
    engine_on: bool
    phase: str
    complete: bool = False


class _LaunchController:
    def __init__(self, initial: FlightState, settings: OrbitLaunchSettings) -> None:
        self.settings = settings
        vehicle, propulsion = self._require_state(initial)
        body = vehicle.parent_body
        if body is None or body.mean_radius_m is None or body.gravitational_parameter_m3ps2 is None:
            raise OrbitLaunchError("body radius and gravitational parameter telemetry are required")
        self.body_radius = body.mean_radius_m
        self.mu = body.gravitational_parameter_m3ps2
        if self.body_radius <= 0 or self.mu <= 0:
            raise OrbitLaunchError("body radius and gravitational parameter must be positive")
        self.vehicle_id, self.body_id = vehicle.id, body.id
        self.initial_time = initial.player_time_s
        self.last_time = initial.player_time_s
        self.initial_radius = norm(vehicle.position_cci_m)
        self.target_radius = self.body_radius + settings.target_altitude_m
        self.phase = "ascent"
        if self.initial_radius >= self.target_radius - settings.orbit_tolerance_m:
            raise OrbitLaunchError("launch must start below the target orbit")
        if initial.surface is None or not (initial.surface.landed or initial.surface.ground_contact):
            raise OrbitLaunchError("launch must start on the ground; land and select the prepared rocket")
        self._check_thrust(vehicle, propulsion)
        _orbit(vehicle.position_cci_m, vehicle.velocity_cci_mps, self.mu)
        up = normalize_vector(vehicle.position_cci_m)
        if settings.launch_direction_cci is not None:
            horizontal = settings.launch_direction_cci
        else:
            if vehicle.surface_velocity_cci_mps is None:
                raise OrbitLaunchError("surface velocity is required to infer east; set launch_direction_cci")
            east = _add(vehicle.velocity_cci_mps, _scale(vehicle.surface_velocity_cci_mps, -1))
            east = _add(east, _scale(up, -dot(east, up)))
            if norm(east) < 1e-6:
                raise OrbitLaunchError("local east is undefined here; set launch_direction_cci explicitly")
            east = normalize_vector(east)
            north = normalize_vector(cross(up, east))
            heading = math.radians(settings.heading_deg)
            horizontal = _add(_scale(east, math.sin(heading)), _scale(north, math.cos(heading)))
        horizontal = _add(horizontal, _scale(up, -dot(horizontal, up)))
        if norm(horizontal) < 1e-6:
            raise OrbitLaunchError("launch_direction_cci must have a horizontal component")
        self.plane_normal = normalize_vector(cross(up, horizontal))
        self._stable_since: float | None = None
        self._throttle_credit = 0.0

    @staticmethod
    def _require_state(state: FlightState):
        if state.simulation_speed is None or not 0 <= state.simulation_speed <= 4:
            raise OrbitLaunchError("use simulation speed from paused to 4x for this controller; start at 1x")
        vehicle = state.vehicle
        if (
            vehicle is None
            or not vehicle.controllable
            or vehicle.position_cci_m is None
            or vehicle.velocity_cci_mps is None
            or vehicle.body_to_cci is None
            or vehicle.total_mass_kg is None
            or vehicle.total_mass_kg <= 0
            or state.propulsion is None
        ):
            raise OrbitLaunchError(
                "controllable vehicle, CCI position/velocity, orientation, mass, and propulsion are required"
            )
        return vehicle, state.propulsion

    def _check_thrust(self, vehicle, propulsion) -> float:
        maximum = propulsion.maximum_available_thrust_n
        if maximum is None or maximum <= 0:
            raise OrbitLaunchError("no available engine thrust; prepare the engine stage before running this example")
        acceleration = maximum / vehicle.total_mass_kg
        gravity = self.mu / norm(vehicle.position_cci_m) ** 2
        if self.phase == "ascent" and acceleration < 1.1 * gravity:
            raise OrbitLaunchError("available thrust-to-weight is below 1.1; prepare a suitable rocket or next stage")
        if self.phase == "ascent" and self.settings.maximum_acceleration_mps2 < 1.1 * gravity:
            raise OrbitLaunchError("maximum_acceleration_mps2 is too low for lift-off on this body")
        if propulsion.minimum_throttle is None or not 0 <= propulsion.minimum_throttle <= 1:
            raise OrbitLaunchError("minimum-throttle telemetry is required")
        if propulsion.minimum_throttle * acceleration > self.settings.maximum_acceleration_mps2:
            raise OrbitLaunchError(
                "engine minimum throttle exceeds the acceleration limit; adjust the engine stage or profile"
            )
        if any(engine.staged_enabled and engine.restart_available is False for engine in propulsion.engines):
            raise OrbitLaunchError("this coast-and-restart example requires restartable enabled engines")
        return acceleration

    def step(self, state: FlightState) -> _Command:
        vehicle, propulsion = self._require_state(state)
        if vehicle.id != self.vehicle_id or vehicle.parent_body is None or vehicle.parent_body.id != self.body_id:
            raise OrbitLaunchError("vehicle or parent body changed during launch")
        if state.player_time_s < self.last_time:
            raise OrbitLaunchError("simulation clock moved backwards during launch")
        if state.player_time_s - self.initial_time > self.settings.maximum_sim_seconds:
            raise OrbitLaunchError("launch exceeded its simulation-time limit")
        self.last_time = state.player_time_s
        orbit = _orbit(vehicle.position_cci_m, vehicle.velocity_cci_mps, self.mu)
        up = normalize_vector(vehicle.position_cci_m)
        tangent = normalize_vector(cross(self.plane_normal, up))
        maximum_accel = self._check_thrust(vehicle, propulsion)
        if self.phase == "ascent" and orbit.apoapsis_radius_m >= self.target_radius:
            self.phase = "coast"
        if self.phase != "ascent":
            horizontal_velocity = _add(vehicle.velocity_cci_mps, _scale(up, -orbit.radial_speed_mps))
            if norm(horizontal_velocity) > 1e-6:
                tangent = normalize_vector(horizontal_velocity)
        if self.phase == "coast":
            speed_at_apoapsis = norm(cross(vehicle.position_cci_m, vehicle.velocity_cci_mps)) / orbit.apoapsis_radius_m
            delta_v = max(0.0, math.sqrt(self.mu / orbit.apoapsis_radius_m) - speed_at_apoapsis)
            burn_accel = min(maximum_accel, self.settings.maximum_acceleration_mps2)
            if orbit.radial_speed_mps <= 0 or orbit.seconds_to_apoapsis <= delta_v / burn_accel / 2 + 5:
                self.phase = "circularization"
            else:
                return _Command(tangent, 0.0, False, self.phase)
        if self.phase == "ascent":
            height = max(0.0, orbit.radius_m - self.initial_radius)
            fraction = _clamp(
                (height - self.settings.turn_start_height_m)
                / (self.settings.turn_end_height_m - self.settings.turn_start_height_m),
                0.0,
                1.0,
            )
            pitch = math.pi / 2 * (1 - fraction)
            direction = normalize_vector(_add(_scale(up, math.sin(pitch)), _scale(tangent, math.cos(pitch))))
            throttle = min(1.0, self.settings.maximum_acceleration_mps2 / maximum_accel)
            remaining = self.target_radius - orbit.apoapsis_radius_m
            throttle *= _clamp(remaining / 10_000.0, 0.1, 1.0)
            if height < 10 and state.player_time_s - self.initial_time > 30:
                raise OrbitLaunchError("rocket did not lift off within 30 simulation seconds")
        else:
            if (
                orbit.radius_m < self.body_radius + self.settings.target_altitude_m * 0.5
                and orbit.radial_speed_mps < -50
            ):
                raise OrbitLaunchError("descending below the circularization flight envelope")
            target_speed = math.sqrt(self.mu / self.target_radius)
            target_radial = _clamp((self.target_radius - orbit.radius_m) / 30, -100.0, 100.0)
            radial_accel = self.mu / orbit.radius_m**2 - orbit.tangential_speed_mps**2 / orbit.radius_m
            radial_accel += (target_radial - orbit.radial_speed_mps) / 10
            tangent_accel = (target_speed - orbit.tangential_speed_mps) / 15
            tangent_accel += orbit.radial_speed_mps * orbit.tangential_speed_mps / orbit.radius_m
            requested = _add(_scale(up, radial_accel), _scale(tangent, tangent_accel))
            magnitude = norm(requested)
            direction = tangent if magnitude < 1e-8 else normalize_vector(requested)
            throttle = min(1.0, self.settings.maximum_acceleration_mps2 / maximum_accel, magnitude / maximum_accel)
            in_orbit = (
                abs(orbit.apoapsis_radius_m - self.target_radius) <= self.settings.orbit_tolerance_m
                and abs(orbit.periapsis_radius_m - self.target_radius) <= self.settings.orbit_tolerance_m
                and abs(orbit.radial_speed_mps) <= 5
            )
            if not in_orbit:
                self._stable_since = None
            elif self._stable_since is None:
                self._stable_since = state.player_time_s
            elif state.player_time_s - self._stable_since >= 2:
                return _Command(direction, 0.0, False, "complete", True)
        forward = rotate_vector(Vector3(1.0, 0.0, 0.0), vehicle.body_to_cci)
        alignment = dot(forward, direction)
        throttle *= max(0.0, alignment) if alignment >= math.cos(math.radians(45)) else 0.0
        minimum = propulsion.minimum_throttle
        if 0 < throttle < minimum:
            self._throttle_credit += throttle / minimum
            throttle = minimum if self._throttle_credit >= 1 else 0.0
            if throttle:
                self._throttle_credit -= 1
        return _Command(direction, throttle, True, self.phase)

    def result(self, state: FlightState) -> OrbitInsertionResult:
        vehicle, _ = self._require_state(state)
        if vehicle.id != self.vehicle_id or vehicle.parent_body is None or vehicle.parent_body.id != self.body_id:
            raise OrbitLaunchError("vehicle or parent body changed before insertion confirmation")
        orbit = _orbit(vehicle.position_cci_m, vehicle.velocity_cci_mps, self.mu)
        return OrbitInsertionResult(
            self.vehicle_id,
            self.body_id,
            orbit.apoapsis_radius_m - self.body_radius,
            orbit.periapsis_radius_m - self.body_radius,
            state.player_time_s - self.initial_time,
        )


def launch_to_orbit(
    ksa: KsaApi,
    settings: OrbitLaunchSettings | None = None,
    *,
    on_phase: Callable[[str], None] | None = None,
) -> OrbitInsertionResult:
    """Experimental launch of the prepared active rocket into a circular orbit.

    Default target is 200 km above the parent body's mean radius, heading east.
    Requires an enabled engine stage, restartable/throttleable propulsion,
    attitude control, and sufficient propellant. Stage changes are explicit
    player actions; this helper never guesses a staging sequence.
    """
    settings = OrbitLaunchSettings() if settings is None else settings
    if not isinstance(settings, OrbitLaunchSettings):
        raise TypeError("settings must be OrbitLaunchSettings")
    if on_phase is not None and not callable(on_phase):
        raise TypeError("on_phase must be callable")
    capabilities = ksa.capabilities.control
    if not (capabilities.thrust_vector_target and capabilities.exact_throttle and capabilities.engine_state):
        raise UnsupportedCapabilityError("launch requires thrust-vector, exact-throttle, and engine-state control")
    initial = ksa.data
    if initial is None:
        raise OrbitLaunchError("flight telemetry is unavailable")
    controller = _LaunchController(initial, settings)
    vehicle = ksa.active_vehicle
    if vehicle is None or vehicle.id != controller.vehicle_id:
        raise OrbitLaunchError("the prepared active vehicle changed before launch")
    deadline = time.monotonic() + settings.maximum_real_seconds
    phase = None
    state = initial
    command = _Command(normalize_vector(initial.vehicle.position_cci_m), 0.0, False, "preflight")
    with vehicle.control.leased() as control:

        def shutdown() -> FlightState:
            if not control.lease_active:
                raise OrbitLaunchError("control lease was lost; final engine-off cannot be confirmed")
            telemetry = control.telemetry
            applied = None if telemetry is None or telemetry.lease_id != control.lease_id else telemetry.applied
            previous_seq = -1 if applied is None else applied.control_seq
            lease_id = control.lease_id
            control.set_thrust_vector(command.direction, engine_on=False, throttle=0.0)
            return ksa.wait_until(
                lambda snapshot: (
                    snapshot.vehicle is not None
                    and snapshot.vehicle.id == vehicle.id
                    and snapshot.applied_control.lease_active
                    and snapshot.applied_control.lease_id == lease_id
                    and snapshot.applied_control.applied is not None
                    and snapshot.applied_control.applied.control_seq > previous_seq
                    and snapshot.applied_control.applied.engine_on is False
                    and snapshot.applied_control.applied.throttle == 0.0
                ),
                timeout=settings.telemetry_timeout_s,
                description="the final engine-off command to be applied",
            )

        try:
            while True:
                if time.monotonic() >= deadline:
                    raise OrbitLaunchError("launch exceeded its real-time limit")
                command = controller.step(state)
                if command.phase != phase:
                    phase = command.phase
                    if on_phase is not None:
                        on_phase(phase)
                if command.complete:
                    break
                control.set_thrust_vector(command.direction, engine_on=command.engine_on, throttle=command.throttle)
                frame = state.frame_number
                state = ksa.wait_until(
                    lambda snapshot: snapshot.frame_number != frame,
                    timeout=settings.telemetry_timeout_s,
                    description="fresh launch telemetry",
                )
        except BaseException as exc:
            try:
                shutdown()
            except Exception as shutdown_error:
                exc.add_note(f"The final engine-off command could not be confirmed: {shutdown_error}")
            raise
        final = shutdown()
    result = controller.result(final)
    if (
        abs(result.apoapsis_altitude_m - settings.target_altitude_m) > settings.orbit_tolerance_m
        or abs(result.periapsis_altitude_m - settings.target_altitude_m) > settings.orbit_tolerance_m
    ):
        raise OrbitLaunchError("the confirmed engine cutoff left the orbit outside the requested tolerance")
    return result
