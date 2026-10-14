"""Idealized Earth-scale trajectory tests, not live KSA acceptance tests."""

from __future__ import annotations

import math
from dataclasses import replace

import pytest
from kittenio import Vector3
from kittenio.api.orbit import OrbitLaunchError, OrbitLaunchSettings, _LaunchController, _orbit

from python.tests.support.orbit_simulation import MU, RADIUS, IdealLaunchPlant, prepared_state


def test_osculating_circular_orbit_has_equal_apsides():
    radius = RADIUS + 200_000
    orbit = _orbit(Vector3(radius, 0, 0), Vector3(0, math.sqrt(MU / radius), 0), MU)
    assert orbit.apoapsis_radius_m == pytest.approx(radius, abs=0.1)
    assert orbit.periapsis_radius_m == pytest.approx(radius, abs=0.1)
    assert orbit.radial_speed_mps == 0


def test_transfer_apsides_and_time_are_measured_from_the_body_centre():
    periapsis, apoapsis = RADIUS + 100_000, RADIUS + 200_000
    major_axis = (periapsis + apoapsis) / 2
    velocity = math.sqrt(MU * (2 / periapsis - 1 / major_axis))
    orbit = _orbit(Vector3(periapsis, 0, 0), Vector3(0, velocity, 0), MU)
    assert orbit.periapsis_radius_m == pytest.approx(periapsis, abs=0.1)
    assert orbit.apoapsis_radius_m == pytest.approx(apoapsis, abs=0.1)
    assert orbit.seconds_to_apoapsis == pytest.approx(math.pi * math.sqrt(major_axis**3 / MU))


@pytest.mark.parametrize("attitude_lag_s", [0.0, 0.5])
def test_ideal_earth_launch_reaches_a_200_km_orbit(flight_state, attitude_lag_s):
    initial = prepared_state(flight_state)
    controller = _LaunchController(initial, OrbitLaunchSettings())
    plant = IdealLaunchPlant(initial, attitude_lag_s=attitude_lag_s)
    phases = set()
    for _ in range(27000):
        state = plant.state()
        command = controller.step(state)
        phases.add(command.phase)
        if command.complete:
            result = controller.result(state)
            assert abs(result.apoapsis_altitude_m - 200_000) <= 5000
            assert abs(result.periapsis_altitude_m - 200_000) <= 5000
            assert plant.mass > plant.dry_mass
            assert {"ascent", "coast", "circularization", "complete"} <= phases
            return
        plant.step(command)
    pytest.fail(f"insertion did not complete; phase={controller.phase}, state={controller.result(plant.state())}")


@pytest.mark.parametrize("field", ["target_altitude_m", "maximum_sim_seconds", "telemetry_timeout_s"])
@pytest.mark.parametrize("value", [0, -1, float("nan"), float("inf"), True])
def test_launch_settings_reject_invalid_limits(field, value):
    with pytest.raises(ValueError):
        OrbitLaunchSettings(**{field: value})


def test_preflight_rejects_missing_dynamics_and_insufficient_thrust(flight_state):
    state = prepared_state(flight_state)
    with pytest.raises(OrbitLaunchError, match="CCI position"):
        _LaunchController(replace(state, vehicle=replace(state.vehicle, position_cci_m=None)), OrbitLaunchSettings())
    with pytest.raises(OrbitLaunchError, match="thrust-to-weight"):
        weak = replace(state, propulsion=replace(state.propulsion, maximum_available_thrust_n=100))
        _LaunchController(weak, OrbitLaunchSettings())


def test_preflight_rejects_an_airborne_vehicle(flight_state):
    state = prepared_state(flight_state)
    state = replace(state, surface=replace(state.surface, landed=False, ground_contact=False))
    with pytest.raises(OrbitLaunchError, match="on the ground"):
        _LaunchController(state, OrbitLaunchSettings())


def test_missing_local_east_requires_an_explicit_direction(flight_state):
    state = prepared_state(flight_state)
    state = replace(state, vehicle=replace(state.vehicle, surface_velocity_cci_mps=state.vehicle.velocity_cci_mps))
    with pytest.raises(OrbitLaunchError, match="launch_direction_cci"):
        _LaunchController(state, OrbitLaunchSettings())
    _LaunchController(state, OrbitLaunchSettings(launch_direction_cci=Vector3(0, 1, 0)))


def test_clock_reset_and_vehicle_switch_abort_the_controller(flight_state):
    state = prepared_state(flight_state)
    controller = _LaunchController(state, OrbitLaunchSettings())
    with pytest.raises(OrbitLaunchError, match="clock moved backwards"):
        controller.step(replace(state, player_time_s=state.player_time_s - 1))
    with pytest.raises(OrbitLaunchError, match="vehicle or parent"):
        controller.step(replace(state, vehicle=replace(state.vehicle, id="another-vehicle")))


def test_high_time_warp_and_unusable_acceleration_limits_are_rejected(flight_state):
    state = prepared_state(flight_state)
    with pytest.raises(OrbitLaunchError, match="simulation speed"):
        _LaunchController(replace(state, simulation_speed=120), OrbitLaunchSettings())
    with pytest.raises(OrbitLaunchError, match="too low for lift-off"):
        _LaunchController(state, OrbitLaunchSettings(maximum_acceleration_mps2=1))


def test_explicitly_unrestartable_engines_and_missing_throttle_limits_are_rejected(flight_state):
    state = prepared_state(flight_state)
    engine = replace(flight_state.propulsion.engines[0], staged_enabled=True, restart_available=False)
    with pytest.raises(OrbitLaunchError, match="restartable"):
        bad = replace(state, propulsion=replace(state.propulsion, engines=(engine,)))
        _LaunchController(bad, OrbitLaunchSettings())
    with pytest.raises(OrbitLaunchError, match="minimum-throttle"):
        bad = replace(state, propulsion=replace(state.propulsion, minimum_throttle=None))
        _LaunchController(bad, OrbitLaunchSettings())


def test_ascent_suppresses_throttle_when_pointing_far_from_the_commanded_direction(flight_state):
    from kittenio import Quaternion

    state = prepared_state(flight_state)
    state = replace(state, vehicle=replace(state.vehicle, body_to_cci=Quaternion(0, 0, 1, 0)))
    command = _LaunchController(state, OrbitLaunchSettings()).step(state)
    assert command.throttle == 0


def test_a_stuck_rocket_and_a_timed_out_launch_fail_explicitly(flight_state):
    state = prepared_state(flight_state)
    controller = _LaunchController(state, OrbitLaunchSettings())
    with pytest.raises(OrbitLaunchError, match="did not lift off"):
        controller.step(replace(state, player_time_s=state.player_time_s + 31))
    controller = _LaunchController(state, OrbitLaunchSettings(maximum_sim_seconds=10))
    with pytest.raises(OrbitLaunchError, match="simulation-time limit"):
        controller.step(replace(state, player_time_s=state.player_time_s + 11))


def test_an_escape_trajectory_is_not_reported_as_an_orbit():
    with pytest.raises(OrbitLaunchError, match="unbound"):
        _orbit(Vector3(RADIUS, 0, 0), Vector3(0, math.sqrt(2 * MU / RADIUS) * 1.01, 0), MU)


def test_a_frame_just_after_apoapsis_starts_insertion_instead_of_waiting_another_orbit(flight_state):
    initial = prepared_state(flight_state)
    controller = _LaunchController(initial, OrbitLaunchSettings())
    controller.phase = "coast"
    radius = RADIUS + 200_000
    late = replace(
        initial,
        vehicle=replace(
            initial.vehicle,
            position_cci_m=Vector3(radius, 0, 0),
            velocity_cci_mps=Vector3(-1, math.sqrt(MU / radius) - 30, 0),
        ),
    )
    assert controller.step(late).phase == "circularization"
