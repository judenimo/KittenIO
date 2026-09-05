"""Small, dependency-free estimators for recorded landing data."""

from __future__ import annotations

import math
from dataclasses import dataclass
from statistics import mean
from typing import Iterable

from ..wire.models import FlightState
from .transforms import cross, dot, norm, rotate_vector


@dataclass(frozen=True, slots=True)
class ThrottleThrustFit:
    gain_n: float
    intercept_n: float
    rms_error_n: float
    sample_count: int


def estimate_throttle_to_thrust(states: Iterable[FlightState]) -> ThrottleThrustFit:
    samples: list[tuple[float, float]] = []
    for state in states:
        p = state.propulsion
        if p is not None and p.applied_throttle is not None and p.current_thrust_body_n is not None:
            samples.append((p.applied_throttle, norm(p.current_thrust_body_n)))
    if len(samples) < 2:
        raise ValueError("at least two throttle/thrust samples are required")
    xbar, ybar = mean(x for x, _ in samples), mean(y for _, y in samples)
    denominator = sum((x - xbar) ** 2 for x, _ in samples)
    if denominator <= 1e-15:
        raise ValueError("throttle samples must vary")
    gain = sum((x - xbar) * (y - ybar) for x, y in samples) / denominator
    intercept = ybar - gain * xbar
    rms = math.sqrt(mean((y - (gain * x + intercept)) ** 2 for x, y in samples))
    return ThrottleThrustFit(gain, intercept, rms, len(samples))


def estimate_effective_isp_s(states: Iterable[FlightState], *, standard_gravity_mps2: float = 9.80665) -> float:
    values: list[float] = []
    for state in states:
        p = state.propulsion
        if (
            p is not None
            and p.current_thrust_body_n is not None
            and p.mass_flow_rate_kgps is not None
            and p.mass_flow_rate_kgps > 0
        ):
            values.append(norm(p.current_thrust_body_n) / (p.mass_flow_rate_kgps * standard_gravity_mps2))
    if not values:
        raise ValueError("no powered mass-flow samples are available")
    return mean(values)


def estimate_effective_mass_flow_kgps(states: Iterable[FlightState]) -> float:
    """Estimate powered propellant flow from successive mass/time observations."""

    values: list[float] = []
    previous: tuple[str, float, float] | None = None
    for state in states:
        vehicle, propulsion = state.vehicle, state.propulsion
        if vehicle is None or vehicle.propellant_mass_kg is None:
            previous = None
            continue
        current = (vehicle.id, state.player_time_s, vehicle.propellant_mass_kg)
        if (
            previous is not None
            and previous[0] == current[0]
            and propulsion is not None
            and propulsion.mass_flow_rate_kgps is not None
            and propulsion.mass_flow_rate_kgps > 0
        ):
            elapsed = current[1] - previous[1]
            consumed = previous[2] - current[2]
            if elapsed > 0 and consumed >= 0:
                values.append(consumed / elapsed)
        previous = current
    if not values:
        raise ValueError("no successive powered propellant-mass samples are available")
    return mean(values)


def estimate_ignition_delays_s(
    states: Iterable[FlightState],
    *,
    thrust_threshold_n: float = 1.0,
) -> tuple[float, ...]:
    """Measure applied engine-on transitions to first observed aggregate thrust."""

    if not math.isfinite(thrust_threshold_n) or thrust_threshold_n < 0:
        raise ValueError("thrust_threshold_n must be finite and non-negative")
    delays: list[float] = []
    previous_override: bool | None = None
    pending_ms: int | None = None
    for state in states:
        applied = state.applied_control.applied
        if applied is not None:
            if applied.engine_on is True and previous_override is not True:
                pending_ms = applied.monotonic_ms
            elif applied.engine_on is False:
                pending_ms = None
            if applied.engine_on is not None:
                previous_override = applied.engine_on
        propulsion = state.propulsion
        thrust = (
            None
            if propulsion is None or propulsion.current_thrust_body_n is None
            else norm(propulsion.current_thrust_body_n)
        )
        if (
            pending_ms is not None
            and thrust is not None
            and thrust >= thrust_threshold_n
            and state.captured_monotonic_ms >= pending_ms
        ):
            delays.append((state.captured_monotonic_ms - pending_ms) / 1000.0)
            pending_ms = None
    return tuple(delays)


def estimate_spool_time_constants_s(
    states: Iterable[FlightState],
    *,
    response_fraction: float = 1.0 - math.exp(-1.0),
) -> tuple[float, ...]:
    """Estimate first-order throttle-step time constants from 63.2% crossings."""

    if not math.isfinite(response_fraction) or not 0 < response_fraction < 1:
        raise ValueError("response_fraction must be finite and in (0, 1)")
    results: list[float] = []
    previous_throttle: float | None = None
    pending: tuple[int, float] | None = None
    for state in states:
        applied = state.applied_control.applied
        propulsion = state.propulsion
        if (
            applied is not None
            and applied.throttle is not None
            and propulsion is not None
            and propulsion.maximum_available_thrust_n is not None
        ):
            throttle = applied.throttle
            if previous_throttle is None or throttle > previous_throttle + 1e-9:
                initial = 0.0 if propulsion.current_thrust_body_n is None else norm(propulsion.current_thrust_body_n)
                target = throttle * propulsion.maximum_available_thrust_n
                if target > initial:
                    pending = (applied.monotonic_ms, initial + response_fraction * (target - initial))
            elif throttle < previous_throttle - 1e-9:
                pending = None
            previous_throttle = throttle
        thrust = (
            None
            if propulsion is None or propulsion.current_thrust_body_n is None
            else norm(propulsion.current_thrust_body_n)
        )
        if (
            pending is not None
            and thrust is not None
            and thrust >= pending[1]
            and state.captured_monotonic_ms >= pending[0]
        ):
            results.append((state.captured_monotonic_ms - pending[0]) / 1000.0)
            pending = None
    return tuple(results)


def command_application_delays_s(states: Iterable[FlightState]) -> tuple[float, ...]:
    delays = []
    for state in states:
        feedback = state.applied_control
        if (
            feedback.accepted is not None
            and feedback.applied is not None
            and feedback.accepted.control_seq == feedback.applied.control_seq
        ):
            delays.append((feedback.applied.monotonic_ms - feedback.accepted.monotonic_ms) / 1000.0)
    return tuple(delays)


def acceleration_prediction_errors_mps2(states: Iterable[FlightState]) -> tuple[float, ...]:
    """Compare measured non-gravity acceleration with aggregate thrust / mass in CCI."""

    errors = []
    for state in states:
        vehicle, propulsion = state.vehicle, state.propulsion
        if (
            vehicle is None
            or propulsion is None
            or vehicle.total_mass_kg in (None, 0)
            or vehicle.non_gravitational_acceleration_cci_mps2 is None
            or propulsion.current_thrust_cci_n is None
        ):
            continue
        predicted = type(propulsion.current_thrust_cci_n)(
            propulsion.current_thrust_cci_n.x / vehicle.total_mass_kg,
            propulsion.current_thrust_cci_n.y / vehicle.total_mass_kg,
            propulsion.current_thrust_cci_n.z / vehicle.total_mass_kg,
        )
        measured = vehicle.non_gravitational_acceleration_cci_mps2
        errors.append(
            norm(type(measured)(measured.x - predicted.x, measured.y - predicted.y, measured.z - predicted.z))
        )
    return tuple(errors)


def validate_coordinate_conventions(state: FlightState, *, tolerance: float = 1e-6) -> tuple[str, ...]:
    """Return concrete frame/quaternion consistency problems for one live/replayed frame."""

    if not math.isfinite(tolerance) or tolerance <= 0:
        raise ValueError("tolerance must be finite and positive")
    problems: list[str] = []
    vehicle, propulsion, landing = state.vehicle, state.propulsion, state.landing_reference
    if vehicle is not None and vehicle.body_to_cci is not None:
        quaternion_norm = math.sqrt(
            vehicle.body_to_cci.x**2 + vehicle.body_to_cci.y**2 + vehicle.body_to_cci.z**2 + vehicle.body_to_cci.w**2
        )
        if abs(quaternion_norm - 1.0) > tolerance:
            problems.append("body_to_cci is not normalized")
    if landing is not None:
        basis = (landing.east_cci, landing.north_cci, landing.up_cci)
        if all(axis is not None for axis in basis):
            east, north, up = basis
            assert east is not None and north is not None and up is not None
            if any(abs(norm(axis) - 1.0) > tolerance for axis in basis):
                problems.append("landing ENU basis is not unit length")
            if any(abs(value) > tolerance for value in (dot(east, north), dot(east, up), dot(north, up))):
                problems.append("landing ENU basis is not orthogonal")
            handed = cross(east, north)
            if norm(type(up)(handed.x - up.x, handed.y - up.y, handed.z - up.z)) > tolerance:
                problems.append("landing ENU basis is not right-handed")
    if (
        vehicle is not None
        and vehicle.body_to_cci is not None
        and propulsion is not None
        and propulsion.current_thrust_body_n is not None
        and propulsion.current_thrust_cci_n is not None
    ):
        predicted = rotate_vector(propulsion.current_thrust_body_n, vehicle.body_to_cci)
        observed = propulsion.current_thrust_cci_n
        error = norm(type(observed)(predicted.x - observed.x, predicted.y - observed.y, predicted.z - observed.z))
        scale = max(1.0, norm(observed))
        if error > tolerance * scale:
            problems.append("body-to-CCI thrust vectors are inconsistent")
    return tuple(problems)
