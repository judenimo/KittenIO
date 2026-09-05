from __future__ import annotations

import copy
from dataclasses import FrozenInstanceError

import pytest
from kittenio import (
    AxisCommand,
    CapabilitySet,
    FlightState,
    ManualControlState,
    ModelError,
    ThrustVectorControl,
    Vector3,
)


def test_canonical_state_retains_navigation_and_manual_telemetry(flight_state: FlightState) -> None:
    state = flight_state
    assert state.vehicle is not None
    assert state.vehicle.target_id == "body:moon:2"
    assert state.vehicle.target is not None and state.vehicle.target.name == "Moon"
    assert state.vehicle.position_orb_m == Vector3(100.0, 0.0, 0.0)
    assert state.vehicle.velocity_orb_mps == Vector3(-12.0, 0.0, 0.0)
    assert state.vehicle.total_acceleration_cce_mps2 == Vector3(-9.1, 0.0, 0.0)
    assert state.vehicle.total_acceleration_orb_mps2 == Vector3(-9.1, 0.0, 0.0)
    assert state.vehicle.inertial_speed_mps == 12.0
    assert state.vehicle.surface_speed_mps == 12.0
    assert state.vehicle.orbit is not None and state.vehicle.orbit.period_s == 1800.0
    assert state.manual_control == ManualControlState(
        True,
        0.45,
        AxisCommand.NEUTRAL,
        AxisCommand.POSITIVE,
        AxisCommand.NEGATIVE,
        AxisCommand.POSITIVE,
        AxisCommand.NEUTRAL,
        AxisCommand.NEGATIVE,
    )
    assert state.capabilities.time_warp.control
    assert state.capabilities.time_warp.available_rates == (0.0, 1.0, 2.0, 4.0)


def test_state_is_immutable_and_has_no_compatibility_projections(flight_state: FlightState) -> None:
    with pytest.raises(FrozenInstanceError):
        flight_state.player_time_s = 2.0  # type: ignore[misc]
    for removed in ("active_vehicle", "control", "sim_time_s", "runtime_capabilities"):
        assert not hasattr(flight_state, removed)


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (lambda value: value.pop("player_time_s"), "player_time"),
        (lambda value: value.__setitem__("unknown", 1), "unknown"),
        (lambda value: value["vehicle"].__setitem__("inertial_speed_mps", -1), "inertial_speed"),
        (lambda value: value["manual_control"].__setitem__("pitch", "half"), "pitch"),
        (lambda value: value["capabilities"]["time_warp"].__setitem__("available_rates", [2, 1]), "strictly increasing"),
    ],
)
def test_state_rejects_missing_unknown_and_malformed_fields(
    telemetry_payload: dict[str, object], mutation, message: str
) -> None:
    payload = copy.deepcopy(telemetry_payload)
    mutation(payload)
    with pytest.raises(ModelError, match=message):
        FlightState.from_payload(payload)


def test_capability_set_is_nested_and_strict(capabilities: CapabilitySet) -> None:
    assert capabilities.control.thrust_vector_target
    assert capabilities.landing.site_definition
    assert not hasattr(capabilities.control, "manual_axes")
    assert not hasattr(capabilities.control, "continuous_translation")


def test_thrust_vector_is_the_only_continuous_control_payload() -> None:
    command = ThrustVectorControl(Vector3(0.0, 1.0, 0.0), True, 0.6)
    payload = command.to_payload(
        lease_id="33333333-3333-4333-8333-333333333333", control_seq=9
    )
    assert set(payload) == {
        "lease_id",
        "control_seq",
        "mode",
        "engine_on",
        "throttle",
        "thrust_vector_target",
    }
    assert payload["mode"] == "thrust_vector_target"
    with pytest.raises(ModelError, match="unit vector"):
        ThrustVectorControl(Vector3(0.0, 2.0, 0.0))
