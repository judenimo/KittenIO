"""Strict, immutable models for KittenIO's canonical wire protocol."""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any, Mapping

from .common import (
    ManualControlState,
    ModelError,
    OrbitState,
    Quaternion,
    TargetState,
    Vector3,
    _boolean,
    _mapping,
    _no_unknown,
    _nonnegative_int,
    _number,
    _optional_nonnegative_number,
    _optional_number,
    _optional_str,
)


def _optional_bool(value: object, name: str) -> bool | None:
    return None if value is None else _boolean(value, name)


def _optional_int(value: object, name: str) -> int | None:
    return None if value is None else _nonnegative_int(value, name)


def _optional_vector(value: object, name: str) -> Vector3 | None:
    return None if value is None else Vector3.from_dict(value, name)


def _optional_quaternion(value: object, name: str) -> Quaternion | None:
    if value is None:
        return None
    result = Quaternion.from_dict(value, name)
    norm = math.sqrt(result.x**2 + result.y**2 + result.z**2 + result.w**2)
    if not math.isclose(norm, 1.0, rel_tol=1e-9, abs_tol=1e-9):
        raise ModelError(f"{name} must be normalized")
    return result


def _required_str(data: Mapping[str, Any], key: str, name: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value:
        raise ModelError(f"{name}.{key} must be a non-empty string")
    return value


def _tuple_of(value: object, item_type: type, name: str) -> tuple:
    if not isinstance(value, list):
        raise ModelError(f"{name} must be an array")
    return tuple(item_type.from_dict(item, f"{name}[{index}]") for index, item in enumerate(value))


@dataclass(frozen=True, slots=True)
class ControlCapabilities:
    exact_throttle: bool
    engine_state: bool
    stage: bool
    set_target: bool
    thrust_vector_target: bool
    deadman_timeout_ms: int

    @classmethod
    def from_dict(cls, value: object) -> "ControlCapabilities":
        data = _mapping(value, "capabilities.control")
        fields = {
            "exact_throttle",
            "engine_state",
            "stage",
            "set_target",
            "thrust_vector_target",
            "deadman_timeout_ms",
        }
        _no_unknown(data, fields, "capabilities.control")
        timeout = _nonnegative_int(data.get("deadman_timeout_ms"), "capabilities.control.deadman_timeout_ms")
        if not 250 <= timeout <= 750:
            raise ModelError("capabilities.control.deadman_timeout_ms must be in [250, 750]")
        return cls(
            **{
                name: _boolean(data.get(name), f"capabilities.control.{name}")
                for name in fields - {"deadman_timeout_ms"}
            },
            deadman_timeout_ms=timeout,
        )

    def require(self, mode: str) -> None:
        """Raise when an explicit control mode is unavailable."""

        supported = {"thrust_vector_target": self.thrust_vector_target}
        if mode not in supported or not supported[mode]:
            raise UnsupportedCapabilityError(f"control mode {mode!r} is unavailable")


class UnsupportedCapabilityError(RuntimeError):
    """Raised before sending a control mode the game did not advertise."""


def _bool_section(value: object, section: str, fields: tuple[str, ...]) -> dict[str, bool]:
    data = _mapping(value, f"capabilities.{section}")
    allowed = frozenset(fields)
    _no_unknown(data, allowed, f"capabilities.{section}")
    return {name: _boolean(data.get(name), f"capabilities.{section}.{name}") for name in fields}


@dataclass(frozen=True, slots=True)
class DynamicsCapabilities:
    gravity_cci: bool
    gravity_cce: bool
    gravity_local: bool
    total_acceleration: bool
    non_gravitational_acceleration: bool
    body_parameters: bool

    @classmethod
    def from_dict(cls, value: object) -> "DynamicsCapabilities":
        return cls(**_bool_section(value, "dynamics", tuple(cls.__dataclass_fields__)))


@dataclass(frozen=True, slots=True)
class PropulsionCapabilities:
    aggregate: bool
    engines: bool
    engine_positions: bool
    gimbal_state: bool
    gimbal_rate_limits: bool
    restart_counts: bool
    response_parameters: bool

    @classmethod
    def from_dict(cls, value: object) -> "PropulsionCapabilities":
        return cls(**_bool_section(value, "propulsion", tuple(cls.__dataclass_fields__)))


@dataclass(frozen=True, slots=True)
class RigidBodyCapabilities:
    center_of_mass: bool
    inertia_tensor: bool
    angular_acceleration: bool
    total_force_moment: bool
    propulsive_force_moment: bool
    aerodynamic_force_moment: bool
    rcs_force_moment: bool
    torque_authority: bool

    @classmethod
    def from_dict(cls, value: object) -> "RigidBodyCapabilities":
        return cls(**_bool_section(value, "rigid_body", tuple(cls.__dataclass_fields__)))


@dataclass(frozen=True, slots=True)
class AtmosphereCapabilities:
    pressure: bool
    density: bool
    temperature: bool
    speed_of_sound: bool
    mach: bool
    dynamic_pressure: bool
    wind: bool
    air_relative_velocity: bool
    angles: bool

    @classmethod
    def from_dict(cls, value: object) -> "AtmosphereCapabilities":
        return cls(**_bool_section(value, "atmosphere", tuple(cls.__dataclass_fields__)))


@dataclass(frozen=True, slots=True)
class LandingCapabilities:
    site_definition: bool
    local_frame: bool
    terrain_height: bool
    ground_normal: bool
    surface_slope: bool

    @classmethod
    def from_dict(cls, value: object) -> "LandingCapabilities":
        return cls(**_bool_section(value, "landing", tuple(cls.__dataclass_fields__)))


@dataclass(frozen=True, slots=True)
class SurfaceCapabilities:
    ground_contact: bool
    touchdown_events: bool
    collision_events: bool
    structural_failure_events: bool
    engine_out_events: bool
    propellant_exhaustion: bool
    controllability: bool
    contact_points: bool
    landing_leg_state: bool

    @classmethod
    def from_dict(cls, value: object) -> "SurfaceCapabilities":
        return cls(**_bool_section(value, "surface", tuple(cls.__dataclass_fields__)))


@dataclass(frozen=True, slots=True)
class TimeWarpCapabilities:
    control: bool
    available_rates: tuple[float, ...]
    ownership: str
    unavailable_reason: str | None = None

    @classmethod
    def from_dict(cls, value: object) -> "TimeWarpCapabilities":
        data = _mapping(value, "capabilities.time_warp")
        fields = frozenset({"control", "available_rates", "ownership", "unavailable_reason"})
        _no_unknown(data, fields, "capabilities.time_warp")
        control = _boolean(data.get("control"), "capabilities.time_warp.control")
        raw_rates = data.get("available_rates")
        if not isinstance(raw_rates, list):
            raise ModelError("capabilities.time_warp.available_rates must be an array")
        rates = tuple(
            _number(rate, f"capabilities.time_warp.available_rates[{index}]") for index, rate in enumerate(raw_rates)
        )
        if any(rate < 0 for rate in rates) or any(right <= left for left, right in zip(rates, rates[1:])):
            raise ModelError("capabilities.time_warp.available_rates must be non-negative and strictly increasing")
        if control != bool(rates):
            raise ModelError(
                "capabilities.time_warp.available_rates must be non-empty exactly when control is available"
            )
        ownership = data.get("ownership")
        if ownership != "game_global_persistent":
            raise ModelError("capabilities.time_warp.ownership must be game_global_persistent")
        unavailable_reason = _optional_str(data.get("unavailable_reason"), "capabilities.time_warp.unavailable_reason")
        if control and unavailable_reason is not None:
            raise ModelError(
                "capabilities.time_warp.unavailable_reason must be null or omitted when control is available"
            )
        if not control and not unavailable_reason:
            raise ModelError("capabilities.time_warp.unavailable_reason is required when control is unavailable")
        return cls(control, rates, ownership, unavailable_reason)


@dataclass(frozen=True, slots=True)
class CapabilitySet:
    control: ControlCapabilities
    dynamics: DynamicsCapabilities
    propulsion: PropulsionCapabilities
    rigid_body: RigidBodyCapabilities
    atmosphere: AtmosphereCapabilities
    landing: LandingCapabilities
    surface: SurfaceCapabilities
    applied_control: bool
    recording_replay: bool
    time_warp: TimeWarpCapabilities

    @classmethod
    def unavailable(cls, reason: str = "not connected") -> "CapabilitySet":
        return cls(
            control=ControlCapabilities(False, False, False, False, False, 750),
            dynamics=DynamicsCapabilities(False, False, False, False, False, False),
            propulsion=PropulsionCapabilities(False, False, False, False, False, False, False),
            rigid_body=RigidBodyCapabilities(False, False, False, False, False, False, False, False),
            atmosphere=AtmosphereCapabilities(False, False, False, False, False, False, False, False, False),
            landing=LandingCapabilities(False, False, False, False, False),
            surface=SurfaceCapabilities(False, False, False, False, False, False, False, False, False),
            applied_control=False,
            recording_replay=False,
            time_warp=TimeWarpCapabilities(False, (), "game_global_persistent", reason),
        )

    @classmethod
    def from_dict(cls, value: object) -> "CapabilitySet":
        data = _mapping(value, "capabilities")
        fields = frozenset(
            {
                "control",
                "dynamics",
                "propulsion",
                "rigid_body",
                "atmosphere",
                "landing",
                "surface",
                "applied_control",
                "recording_replay",
                "time_warp",
            }
        )
        _no_unknown(data, fields, "capabilities")
        return cls(
            control=ControlCapabilities.from_dict(data.get("control")),
            dynamics=DynamicsCapabilities.from_dict(data.get("dynamics")),
            propulsion=PropulsionCapabilities.from_dict(data.get("propulsion")),
            rigid_body=RigidBodyCapabilities.from_dict(data.get("rigid_body")),
            atmosphere=AtmosphereCapabilities.from_dict(data.get("atmosphere")),
            landing=LandingCapabilities.from_dict(data.get("landing")),
            surface=SurfaceCapabilities.from_dict(data.get("surface")),
            applied_control=_boolean(data.get("applied_control"), "capabilities.applied_control"),
            recording_replay=_boolean(data.get("recording_replay"), "capabilities.recording_replay"),
            time_warp=TimeWarpCapabilities.from_dict(data.get("time_warp")),
        )


@dataclass(frozen=True, slots=True)
class Matrix3Symmetric:
    xx: float
    xy: float
    xz: float
    yy: float
    yz: float
    zz: float

    @classmethod
    def from_dict(cls, value: object, name: str = "matrix") -> "Matrix3Symmetric":
        data = _mapping(value, name)
        fields = tuple(cls.__dataclass_fields__)
        _no_unknown(data, frozenset(fields), name)
        return cls(**{key: _number(data.get(key), f"{name}.{key}") for key in fields})


@dataclass(frozen=True, slots=True)
class CelestialBodyDynamics:
    id: str
    gravitational_parameter_m3ps2: float | None
    mean_radius_m: float | None
    rotation_rate_radps: float | None
    rotation_axis_cce: Vector3 | None

    @classmethod
    def from_dict(cls, value: object, name: str = "parent_body") -> "CelestialBodyDynamics":
        data = _mapping(value, name)
        fields = frozenset(cls.__dataclass_fields__)
        _no_unknown(data, fields, name)
        return cls(
            id=_required_str(data, "id", name),
            gravitational_parameter_m3ps2=_optional_nonnegative_number(
                data.get("gravitational_parameter_m3ps2"), f"{name}.gravitational_parameter_m3ps2"
            ),
            mean_radius_m=_optional_nonnegative_number(data.get("mean_radius_m"), f"{name}.mean_radius_m"),
            rotation_rate_radps=_optional_nonnegative_number(
                data.get("rotation_rate_radps"), f"{name}.rotation_rate_radps"
            ),
            rotation_axis_cce=_optional_vector(data.get("rotation_axis_cce"), f"{name}.rotation_axis_cce"),
        )


_VEHICLE_VECTORS = (
    "position_cci_m",
    "position_cce_m",
    "position_orb_m",
    "velocity_cci_mps",
    "velocity_cce_mps",
    "velocity_orb_mps",
    "surface_velocity_cci_mps",
    "body_rates_radps",
    "angular_acceleration_body_radps2",
    "total_acceleration_body_mps2",
    "total_acceleration_cci_mps2",
    "total_acceleration_cce_mps2",
    "total_acceleration_orb_mps2",
    "gravity_cci_mps2",
    "gravity_cce_mps2",
    "non_gravitational_acceleration_body_mps2",
    "non_gravitational_acceleration_cci_mps2",
)


@dataclass(frozen=True, slots=True)
class VehicleState:
    id: str
    name: str
    parent_id: str | None
    target_id: str | None
    target: TargetState | None
    situation: str | None
    controllable: bool
    position_cci_m: Vector3 | None
    position_cce_m: Vector3 | None
    position_orb_m: Vector3 | None
    velocity_cci_mps: Vector3 | None
    velocity_cce_mps: Vector3 | None
    velocity_orb_mps: Vector3 | None
    surface_velocity_cci_mps: Vector3 | None
    body_to_cci: Quaternion | None
    body_rates_radps: Vector3 | None
    angular_acceleration_body_radps2: Vector3 | None
    total_acceleration_body_mps2: Vector3 | None
    total_acceleration_cci_mps2: Vector3 | None
    total_acceleration_cce_mps2: Vector3 | None
    total_acceleration_orb_mps2: Vector3 | None
    gravity_cci_mps2: Vector3 | None
    gravity_cce_mps2: Vector3 | None
    non_gravitational_acceleration_body_mps2: Vector3 | None
    non_gravitational_acceleration_cci_mps2: Vector3 | None
    barometric_altitude_m: float | None
    radar_altitude_m: float | None
    inertial_speed_mps: float | None
    surface_speed_mps: float | None
    total_mass_kg: float | None
    inert_mass_kg: float | None
    propellant_mass_kg: float | None
    parent_body: CelestialBodyDynamics | None
    orbit: OrbitState | None

    @classmethod
    def from_dict(cls, value: object, name: str = "vehicle") -> "VehicleState":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)
        kwargs = {field: _optional_vector(data.get(field), f"{name}.{field}") for field in _VEHICLE_VECTORS}
        return cls(
            id=_required_str(data, "id", name),
            name=_required_str(data, "name", name),
            parent_id=_optional_str(data.get("parent_id"), f"{name}.parent_id"),
            target_id=_optional_str(data.get("target_id"), f"{name}.target_id"),
            target=None if data.get("target") is None else TargetState.from_dict(data["target"]),
            situation=_optional_str(data.get("situation"), f"{name}.situation"),
            controllable=_boolean(data.get("controllable"), f"{name}.controllable"),
            body_to_cci=_optional_quaternion(data.get("body_to_cci"), f"{name}.body_to_cci"),
            barometric_altitude_m=_optional_nonnegative_number(
                data.get("barometric_altitude_m"), f"{name}.barometric_altitude_m"
            ),
            radar_altitude_m=_optional_nonnegative_number(data.get("radar_altitude_m"), f"{name}.radar_altitude_m"),
            inertial_speed_mps=_optional_nonnegative_number(
                data.get("inertial_speed_mps"), f"{name}.inertial_speed_mps"
            ),
            surface_speed_mps=_optional_nonnegative_number(data.get("surface_speed_mps"), f"{name}.surface_speed_mps"),
            total_mass_kg=_optional_nonnegative_number(data.get("total_mass_kg"), f"{name}.total_mass_kg"),
            inert_mass_kg=_optional_nonnegative_number(data.get("inert_mass_kg"), f"{name}.inert_mass_kg"),
            propellant_mass_kg=_optional_nonnegative_number(
                data.get("propellant_mass_kg"), f"{name}.propellant_mass_kg"
            ),
            parent_body=None
            if data.get("parent_body") is None
            else CelestialBodyDynamics.from_dict(data["parent_body"], f"{name}.parent_body"),
            orbit=None if data.get("orbit") is None else OrbitState.from_dict(data["orbit"]),
            **kwargs,
        )


_ENGINE_OPTIONAL_NUMBERS = (
    "requested_throttle",
    "applied_throttle",
    "minimum_throttle",
    "maximum_throttle",
    "current_thrust_n",
    "maximum_available_thrust_n",
    "specific_impulse_s",
    "mass_flow_rate_kgps",
    "ignition_delay_s",
    "shutdown_delay_s",
    "response_time_constant_s",
    "ambient_pressure_pa",
)
_ENGINE_VECTORS = (
    "current_thrust_body_n",
    "current_thrust_cci_n",
    "thrust_position_body_m",
    "nominal_thrust_direction_body",
    "gimbal_angles_rad",
    "gimbal_limits_rad",
    "gimbal_rate_limits_radps",
)


@dataclass(frozen=True, slots=True)
class EngineTelemetry:
    id: str
    name: str
    staged_enabled: bool | None
    ignited: bool | None
    propellant_available: bool | None
    requested_throttle: float | None
    applied_throttle: float | None
    minimum_throttle: float | None
    maximum_throttle: float | None
    current_thrust_n: float | None
    maximum_available_thrust_n: float | None
    current_thrust_body_n: Vector3 | None
    current_thrust_cci_n: Vector3 | None
    thrust_position_body_m: Vector3 | None
    nominal_thrust_direction_body: Vector3 | None
    gimbal_angles_rad: Vector3 | None
    gimbal_limits_rad: Vector3 | None
    specific_impulse_s: float | None
    mass_flow_rate_kgps: float | None
    restart_available: bool | None
    remaining_ignitions: int | None
    gimbal_rate_limits_radps: Vector3 | None
    ignition_delay_s: float | None
    shutdown_delay_s: float | None
    response_time_constant_s: float | None
    ambient_pressure_pa: float | None

    @classmethod
    def from_dict(cls, value: object, name: str = "engine") -> "EngineTelemetry":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)
        numbers = {
            field: _optional_nonnegative_number(data.get(field), f"{name}.{field}")
            for field in _ENGINE_OPTIONAL_NUMBERS
        }
        for field in ("requested_throttle", "applied_throttle", "minimum_throttle", "maximum_throttle"):
            if numbers[field] is not None and numbers[field] > 1:
                raise ModelError(f"{name}.{field} must be in [0, 1]")
        return cls(
            id=_required_str(data, "id", name),
            name=_required_str(data, "name", name),
            staged_enabled=_optional_bool(data.get("staged_enabled"), f"{name}.staged_enabled"),
            ignited=_optional_bool(data.get("ignited"), f"{name}.ignited"),
            propellant_available=_optional_bool(data.get("propellant_available"), f"{name}.propellant_available"),
            restart_available=_optional_bool(data.get("restart_available"), f"{name}.restart_available"),
            remaining_ignitions=_optional_int(data.get("remaining_ignitions"), f"{name}.remaining_ignitions"),
            **{field: _optional_vector(data.get(field), f"{name}.{field}") for field in _ENGINE_VECTORS},
            **numbers,
        )


@dataclass(frozen=True, slots=True)
class GimbalActuatorTelemetry:
    id: str
    parent_part_id: str
    angle_y_rad: float
    angle_z_rad: float
    limit_y_rad: float
    limit_z_rad: float
    rate_limit_y_radps: float | None
    rate_limit_z_radps: float | None

    @classmethod
    def from_dict(cls, value: object, name: str = "gimbal") -> "GimbalActuatorTelemetry":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)
        return cls(
            id=_required_str(data, "id", name),
            parent_part_id=_required_str(data, "parent_part_id", name),
            angle_y_rad=_number(data.get("angle_y_rad"), f"{name}.angle_y_rad"),
            angle_z_rad=_number(data.get("angle_z_rad"), f"{name}.angle_z_rad"),
            limit_y_rad=_number(data.get("limit_y_rad"), f"{name}.limit_y_rad"),
            limit_z_rad=_number(data.get("limit_z_rad"), f"{name}.limit_z_rad"),
            rate_limit_y_radps=_optional_nonnegative_number(
                data.get("rate_limit_y_radps"), f"{name}.rate_limit_y_radps"
            ),
            rate_limit_z_radps=_optional_nonnegative_number(
                data.get("rate_limit_z_radps"), f"{name}.rate_limit_z_radps"
            ),
        )


@dataclass(frozen=True, slots=True)
class PropulsionTelemetry:
    requested_throttle: float | None
    applied_throttle: float | None
    minimum_throttle: float | None
    maximum_throttle: float | None
    current_thrust_body_n: Vector3 | None
    current_thrust_cci_n: Vector3 | None
    maximum_available_thrust_n: float | None
    mass_flow_rate_kgps: float | None
    maximum_available_thrust_body_n: Vector3 | None
    maximum_available_thrust_cci_n: Vector3 | None
    effective_specific_impulse_s: float | None
    engines: tuple[EngineTelemetry, ...]
    gimbals: tuple[GimbalActuatorTelemetry, ...]

    @classmethod
    def from_dict(cls, value: object, name: str = "propulsion") -> "PropulsionTelemetry":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)
        return cls(
            requested_throttle=_optional_number(data.get("requested_throttle"), f"{name}.requested_throttle"),
            applied_throttle=_optional_number(data.get("applied_throttle"), f"{name}.applied_throttle"),
            minimum_throttle=_optional_number(data.get("minimum_throttle"), f"{name}.minimum_throttle"),
            maximum_throttle=_optional_number(data.get("maximum_throttle"), f"{name}.maximum_throttle"),
            current_thrust_body_n=_optional_vector(data.get("current_thrust_body_n"), f"{name}.current_thrust_body_n"),
            current_thrust_cci_n=_optional_vector(data.get("current_thrust_cci_n"), f"{name}.current_thrust_cci_n"),
            maximum_available_thrust_n=_optional_nonnegative_number(
                data.get("maximum_available_thrust_n"), f"{name}.maximum_available_thrust_n"
            ),
            maximum_available_thrust_body_n=_optional_vector(
                data.get("maximum_available_thrust_body_n"), f"{name}.maximum_available_thrust_body_n"
            ),
            maximum_available_thrust_cci_n=_optional_vector(
                data.get("maximum_available_thrust_cci_n"), f"{name}.maximum_available_thrust_cci_n"
            ),
            mass_flow_rate_kgps=_optional_nonnegative_number(
                data.get("mass_flow_rate_kgps"), f"{name}.mass_flow_rate_kgps"
            ),
            effective_specific_impulse_s=_optional_nonnegative_number(
                data.get("effective_specific_impulse_s"), f"{name}.effective_specific_impulse_s"
            ),
            engines=_tuple_of(data.get("engines"), EngineTelemetry, f"{name}.engines"),
            gimbals=_tuple_of(data.get("gimbals"), GimbalActuatorTelemetry, f"{name}.gimbals"),
        )


@dataclass(frozen=True, slots=True)
class RigidBodyTelemetry:
    center_of_mass_vehicle_assembly_m: Vector3 | None
    center_of_mass_body_m: Vector3 | None
    inertia_body_kg_m2: Matrix3Symmetric | None
    total_force_body_n: Vector3 | None
    total_force_cci_n: Vector3 | None
    total_moment_body_nm: Vector3 | None
    propulsive_force_body_n: Vector3 | None
    propulsive_moment_body_nm: Vector3 | None
    aerodynamic_force_body_n: Vector3 | None
    aerodynamic_moment_body_nm: Vector3 | None
    rcs_force_body_n: Vector3 | None
    rcs_moment_body_nm: Vector3 | None
    positive_torque_authority_body_nm: Vector3 | None
    negative_torque_authority_body_nm: Vector3 | None
    tvc_torque_authority_body_nm: Vector3 | None
    rcs_torque_authority_body_nm: Vector3 | None

    @classmethod
    def from_dict(cls, value: object, name: str = "rigid_body") -> "RigidBodyTelemetry":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)
        return cls(
            **{
                field: _optional_vector(data.get(field), f"{name}.{field}")
                for field in cls.__dataclass_fields__
                if field != "inertia_body_kg_m2"
            },
            inertia_body_kg_m2=None
            if data.get("inertia_body_kg_m2") is None
            else Matrix3Symmetric.from_dict(data["inertia_body_kg_m2"], f"{name}.inertia_body_kg_m2"),
        )


@dataclass(frozen=True, slots=True)
class AtmosphereTelemetry:
    pressure_pa: float | None
    density_kgpm3: float | None
    dynamic_pressure_pa: float | None
    air_relative_velocity_body_mps: Vector3 | None
    air_relative_velocity_cci_mps: Vector3 | None
    air_relative_velocity_cce_mps: Vector3 | None
    temperature_k: float | None
    speed_of_sound_mps: float | None
    mach: float | None
    wind_velocity_cci_mps: Vector3 | None
    angle_of_attack_rad: float | None
    sideslip_angle_rad: float | None

    @classmethod
    def from_dict(cls, value: object, name: str = "atmosphere") -> "AtmosphereTelemetry":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)
        vectors = (
            "air_relative_velocity_body_mps",
            "air_relative_velocity_cci_mps",
            "air_relative_velocity_cce_mps",
            "wind_velocity_cci_mps",
        )
        return cls(
            **{field: _optional_vector(data.get(field), f"{name}.{field}") for field in vectors},
            **{
                field: _optional_nonnegative_number(data.get(field), f"{name}.{field}")
                for field in (
                    "pressure_pa",
                    "density_kgpm3",
                    "dynamic_pressure_pa",
                    "temperature_k",
                    "speed_of_sound_mps",
                    "mach",
                )
            },
            angle_of_attack_rad=_optional_number(data.get("angle_of_attack_rad"), f"{name}.angle_of_attack_rad"),
            sideslip_angle_rad=_optional_number(data.get("sideslip_angle_rad"), f"{name}.sideslip_angle_rad"),
        )


@dataclass(frozen=True, slots=True)
class SurfaceTelemetry:
    ground_contact: bool | None
    landed: bool | None
    ground_normal_body: Vector3 | None
    contact_count: int | None
    touchdown_event_seq: int | None
    touchdown_vertical_speed_mps: float | None
    collision_event_seq: int | None
    structural_failure_event_seq: int | None
    engine_out_event_seq: int | None
    propellant_exhausted: bool | None
    controllable: bool

    @classmethod
    def from_dict(cls, value: object, name: str = "surface") -> "SurfaceTelemetry":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)
        return cls(
            ground_contact=_optional_bool(data.get("ground_contact"), f"{name}.ground_contact"),
            landed=_optional_bool(data.get("landed"), f"{name}.landed"),
            ground_normal_body=_optional_vector(data.get("ground_normal_body"), f"{name}.ground_normal_body"),
            contact_count=_optional_int(data.get("contact_count"), f"{name}.contact_count"),
            touchdown_event_seq=_optional_int(data.get("touchdown_event_seq"), f"{name}.touchdown_event_seq"),
            touchdown_vertical_speed_mps=_optional_number(
                data.get("touchdown_vertical_speed_mps"), f"{name}.touchdown_vertical_speed_mps"
            ),
            collision_event_seq=_optional_int(data.get("collision_event_seq"), f"{name}.collision_event_seq"),
            structural_failure_event_seq=_optional_int(
                data.get("structural_failure_event_seq"), f"{name}.structural_failure_event_seq"
            ),
            engine_out_event_seq=_optional_int(data.get("engine_out_event_seq"), f"{name}.engine_out_event_seq"),
            propellant_exhausted=_optional_bool(data.get("propellant_exhausted"), f"{name}.propellant_exhausted"),
            controllable=_boolean(data.get("controllable"), f"{name}.controllable"),
        )


@dataclass(frozen=True, slots=True)
class LandingSiteDefinition:
    body_id: str
    position_ccf_m: Vector3
    latitude_deg: float | None = None
    longitude_deg: float | None = None
    altitude_m: float | None = None

    @classmethod
    def from_dict(cls, value: object, name: str = "landing_site") -> "LandingSiteDefinition":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)
        result = cls(
            body_id=_required_str(data, "body_id", name),
            position_ccf_m=Vector3.from_dict(data.get("position_ccf_m"), f"{name}.position_ccf_m"),
            latitude_deg=_optional_number(data.get("latitude_deg"), f"{name}.latitude_deg"),
            longitude_deg=_optional_number(data.get("longitude_deg"), f"{name}.longitude_deg"),
            altitude_m=_optional_number(data.get("altitude_m"), f"{name}.altitude_m"),
        )
        if result.latitude_deg is not None and not -90 <= result.latitude_deg <= 90:
            raise ModelError(f"{name}.latitude_deg must be in [-90, 90]")
        if result.longitude_deg is not None and not -180 <= result.longitude_deg <= 180:
            raise ModelError(f"{name}.longitude_deg must be in [-180, 180]")
        return result

    def to_payload(self) -> dict[str, object]:
        return {
            "body_id": self.body_id,
            "position_ccf_m": {"x": self.position_ccf_m.x, "y": self.position_ccf_m.y, "z": self.position_ccf_m.z},
            "latitude_deg": self.latitude_deg,
            "longitude_deg": self.longitude_deg,
            "altitude_m": self.altitude_m,
        }


@dataclass(frozen=True, slots=True)
class LandingReferenceTelemetry:
    site: LandingSiteDefinition
    site_position_cci_m: Vector3 | None
    site_position_cce_m: Vector3 | None
    site_position_ccf_m: Vector3 | None
    east_cci: Vector3 | None
    north_cci: Vector3 | None
    up_cci: Vector3 | None
    position_enu_m: Vector3 | None
    velocity_enu_mps: Vector3 | None
    vertical_velocity_mps: float | None
    horizontal_speed_mps: float | None
    distance_m: float | None
    gravity_enu_mps2: Vector3 | None
    ground_normal_enu: Vector3 | None
    terrain_height_m: float | None
    radar_altitude_m: float | None
    surface_slope_rad: float | None
    simulation_time_s: float

    @classmethod
    def from_dict(cls, value: object, name: str = "landing_reference") -> "LandingReferenceTelemetry":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)
        vector_fields = (
            "site_position_cci_m",
            "site_position_cce_m",
            "site_position_ccf_m",
            "east_cci",
            "north_cci",
            "up_cci",
            "position_enu_m",
            "velocity_enu_mps",
            "gravity_enu_mps2",
            "ground_normal_enu",
        )
        return cls(
            site=LandingSiteDefinition.from_dict(data.get("site"), f"{name}.site"),
            **{field: _optional_vector(data.get(field), f"{name}.{field}") for field in vector_fields},
            vertical_velocity_mps=_optional_number(data.get("vertical_velocity_mps"), f"{name}.vertical_velocity_mps"),
            horizontal_speed_mps=_optional_nonnegative_number(
                data.get("horizontal_speed_mps"), f"{name}.horizontal_speed_mps"
            ),
            distance_m=_optional_nonnegative_number(data.get("distance_m"), f"{name}.distance_m"),
            terrain_height_m=_optional_number(data.get("terrain_height_m"), f"{name}.terrain_height_m"),
            radar_altitude_m=_optional_nonnegative_number(data.get("radar_altitude_m"), f"{name}.radar_altitude_m"),
            surface_slope_rad=_optional_nonnegative_number(data.get("surface_slope_rad"), f"{name}.surface_slope_rad"),
            simulation_time_s=_number(data.get("simulation_time_s"), f"{name}.simulation_time_s"),
        )


@dataclass(frozen=True, slots=True)
class ControlCommandTelemetry:
    control_seq: int
    mode: str
    engine_on: bool | None
    throttle: float | None
    thrust_vector_target: Vector3 | None
    monotonic_ms: int

    @classmethod
    def from_dict(cls, value: object, name: str = "control_command") -> "ControlCommandTelemetry":
        data = _mapping(value, name)
        fields = frozenset({"control_seq", "mode", "engine_on", "throttle", "thrust_vector_target", "monotonic_ms"})
        _no_unknown(data, fields, name)
        target = data.get("thrust_vector_target")
        direction = None
        if target is not None:
            target_data = _mapping(target, f"{name}.thrust_vector_target")
            _no_unknown(target_data, frozenset({"direction_cci"}), f"{name}.thrust_vector_target")
            direction = Vector3.from_dict(
                target_data.get("direction_cci"), f"{name}.thrust_vector_target.direction_cci"
            )
            direction_norm = math.sqrt(direction.x**2 + direction.y**2 + direction.z**2)
            if not math.isclose(direction_norm, 1.0, rel_tol=1e-6, abs_tol=1e-6):
                raise ModelError(f"{name}.thrust_vector_target.direction_cci must be a unit vector")
        throttle = _optional_number(data.get("throttle"), f"{name}.throttle")
        if throttle is not None and not 0 <= throttle <= 1:
            raise ModelError(f"{name}.throttle must be in [0, 1]")
        return cls(
            control_seq=_nonnegative_int(data.get("control_seq"), f"{name}.control_seq"),
            mode=_required_str(data, "mode", name),
            engine_on=_optional_bool(data.get("engine_on"), f"{name}.engine_on"),
            throttle=throttle,
            thrust_vector_target=direction,
            monotonic_ms=_nonnegative_int(data.get("monotonic_ms"), f"{name}.monotonic_ms"),
        )


@dataclass(frozen=True, slots=True)
class AppliedControlTelemetry:
    lease_active: bool
    lease_id: str | None
    active_mode: str | None
    latest_requested: ControlCommandTelemetry | None
    accepted: ControlCommandTelemetry | None
    applied: ControlCommandTelemetry | None
    applied_game_frame: int | None
    applied_simulation_time_s: float | None
    rejection_reason: str | None
    saturation_reason: str | None

    @classmethod
    def from_dict(cls, value: object, name: str = "applied_control") -> "AppliedControlTelemetry":
        data = _mapping(value, name)
        _no_unknown(data, frozenset(cls.__dataclass_fields__), name)

        def command(field: str) -> ControlCommandTelemetry | None:
            return (
                None if data.get(field) is None else ControlCommandTelemetry.from_dict(data[field], f"{name}.{field}")
            )

        return cls(
            lease_active=_boolean(data.get("lease_active"), f"{name}.lease_active"),
            lease_id=_optional_str(data.get("lease_id"), f"{name}.lease_id"),
            active_mode=_optional_str(data.get("active_mode"), f"{name}.active_mode"),
            latest_requested=command("latest_requested"),
            accepted=command("accepted"),
            applied=command("applied"),
            applied_game_frame=_optional_int(data.get("applied_game_frame"), f"{name}.applied_game_frame"),
            applied_simulation_time_s=_optional_number(
                data.get("applied_simulation_time_s"), f"{name}.applied_simulation_time_s"
            ),
            rejection_reason=_optional_str(data.get("rejection_reason"), f"{name}.rejection_reason"),
            saturation_reason=_optional_str(data.get("saturation_reason"), f"{name}.saturation_reason"),
        )


@dataclass(frozen=True, slots=True)
class FlightState:
    frame_number: int
    player_time_s: float
    delta_time_s: float
    simulation_speed: float | None
    captured_monotonic_ms: int
    capabilities: CapabilitySet
    vehicle: VehicleState | None
    propulsion: PropulsionTelemetry | None
    rigid_body: RigidBodyTelemetry | None
    atmosphere: AtmosphereTelemetry | None
    surface: SurfaceTelemetry | None
    landing_reference: LandingReferenceTelemetry | None
    applied_control: AppliedControlTelemetry
    manual_control: ManualControlState | None

    @classmethod
    def from_payload(cls, value: object) -> "FlightState":
        data = _mapping(value, "telemetry")
        _no_unknown(data, frozenset(cls.__dataclass_fields__), "telemetry")

        def optional(field: str, item_type: type):
            return None if data.get(field) is None else item_type.from_dict(data[field], field)

        return cls(
            frame_number=_nonnegative_int(data.get("frame_number"), "telemetry.frame_number"),
            player_time_s=_number(data.get("player_time_s"), "telemetry.player_time_s"),
            delta_time_s=_number(data.get("delta_time_s"), "telemetry.delta_time_s"),
            simulation_speed=_optional_number(data.get("simulation_speed"), "telemetry.simulation_speed"),
            captured_monotonic_ms=_nonnegative_int(
                data.get("captured_monotonic_ms"), "telemetry.captured_monotonic_ms"
            ),
            capabilities=CapabilitySet.from_dict(data.get("capabilities")),
            vehicle=optional("vehicle", VehicleState),
            propulsion=optional("propulsion", PropulsionTelemetry),
            rigid_body=optional("rigid_body", RigidBodyTelemetry),
            atmosphere=optional("atmosphere", AtmosphereTelemetry),
            surface=optional("surface", SurfaceTelemetry),
            landing_reference=optional("landing_reference", LandingReferenceTelemetry),
            applied_control=AppliedControlTelemetry.from_dict(data.get("applied_control")),
            manual_control=optional("manual_control", ManualControlState),
        )

    @property
    def applied_command_age_s(self) -> float | None:
        """Age of the applied command on the mod's monotonic clock."""

        applied = self.applied_control.applied
        if applied is None:
            return None
        return max(0.0, self.captured_monotonic_ms - applied.monotonic_ms) / 1_000.0


@dataclass(frozen=True, slots=True)
class FlightClock:
    """Timing projection from one exact immutable :class:`FlightState` frame."""

    frame_number: int
    player_time_s: float
    delta_time_s: float
    simulation_speed: float | None

    @classmethod
    def from_state(cls, state: FlightState) -> "FlightClock":
        return cls(
            frame_number=state.frame_number,
            player_time_s=state.player_time_s,
            delta_time_s=state.delta_time_s,
            simulation_speed=state.simulation_speed,
        )


@dataclass(frozen=True, slots=True)
class ThrustVectorControl:
    direction_cci: Vector3
    engine_on: bool | None = None
    throttle: float | None = None

    def __post_init__(self) -> None:
        norm = math.sqrt(self.direction_cci.x**2 + self.direction_cci.y**2 + self.direction_cci.z**2)
        if not math.isclose(norm, 1.0, rel_tol=1e-6, abs_tol=1e-6):
            raise ModelError("direction_cci must be a finite unit vector")
        if self.engine_on is not None and not isinstance(self.engine_on, bool):
            raise ModelError("engine_on must be boolean or None")
        if self.throttle is not None and not 0 <= _number(self.throttle, "throttle") <= 1:
            raise ModelError("throttle must be in [0, 1]")

    def to_payload(self, *, lease_id: str, control_seq: int) -> dict[str, object]:
        return {
            "lease_id": lease_id,
            "control_seq": control_seq,
            "mode": "thrust_vector_target",
            "engine_on": self.engine_on,
            "throttle": self.throttle,
            "thrust_vector_target": {
                "direction_cci": {"x": self.direction_cci.x, "y": self.direction_cci.y, "z": self.direction_cci.z}
            },
        }
