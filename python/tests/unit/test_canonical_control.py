from __future__ import annotations

from typing import NamedTuple

import pytest
from kittenio import CapabilitySet, ConnectionInfo, FlightState, ThrustVectorControl, Vector3
from kittenio.api.control import ControlClient, ControlError
from kittenio.api.time_warp import TimeWarp
from kittenio.protocol import Envelope
from kittenio.runtime.engine import RequestTimeoutError, RuntimeOverloadError

GAME = "11111111-1111-4111-8111-111111111111"
SESSION = "22222222-2222-4222-8222-222222222222"
LEASE = "33333333-3333-4333-8333-333333333333"


class Session(NamedTuple):
    controller: object
    game_instance_id: str
    session_id: str
    protocol_version: int


class FakeRuntime:
    def __init__(self, state: FlightState, capabilities: CapabilitySet) -> None:
        self.data = state
        self.state = state
        self.capabilities = capabilities
        self.connection_info = ConnectionInfo(True, GAME, SESSION, protocol_version=1)
        self.session = Session(object(), GAME, SESSION, 1)
        self.sent: list[tuple[str, dict[str, object]]] = []

    def _require_active_session(self):
        return self.session, self.connection_info

    def _vehicle_control_context_is_current(self, session, vehicle_id, handle=None):
        return session is self.session and self.data.vehicle is not None and self.data.vehicle.id == vehicle_id

    def _request_in_session(self, session, message_type, payload, **kwargs):
        if message_type == "lease.acquire":
            return Envelope.create(
                "lease.granted",
                game_instance_id=GAME,
                session_id=SESSION,
                seq=1,
                request_id="lease-request",
                payload={"lease_id": LEASE, "vehicle_id": payload["vehicle_id"], "timeout_ms": 750},
            )
        if payload.get("method") == "time_warp.set_rate":
            rate = payload["params"]["rate"]
            return Envelope.create(
                "response",
                game_instance_id=GAME,
                session_id=SESSION,
                seq=2,
                request_id="warp-request",
                payload={
                    "ok": True,
                    "result": {"rate": rate, "previous_rate": 1.0, "confirmed_frame": 2457},
                },
            )
        return Envelope.create(
            "lease.release",
            game_instance_id=GAME,
            session_id=SESSION,
            seq=3,
            request_id="release-request",
            payload={"lease_id": LEASE, "reason": "released"},
        )

    def _send_in_session(self, session, message_type, payload):
        self.sent.append((message_type, payload))


def test_control_requires_a_vector_then_updates_one_canonical_frame(
    flight_state: FlightState, capabilities: CapabilitySet
) -> None:
    runtime = FakeRuntime(flight_state, capabilities)
    control = ControlClient(runtime)
    control.acquire()
    assert control.override is None
    with pytest.raises(ControlError, match="set_thrust_vector"):
        control.set_throttle(0.5)
    control.set_thrust_vector(Vector3(1.0, 0.0, 0.0), engine_on=True, throttle=0.4)
    control.set_throttle(0.6)
    assert isinstance(control.override, ThrustVectorControl)
    assert [message for message, _ in runtime.sent] == ["control", "control"]
    assert runtime.sent[-1][1]["mode"] == "thrust_vector_target"
    assert runtime.sent[-1][1]["throttle"] == 0.6
    assert not hasattr(control, "set_attitude")
    assert not hasattr(control, "set_translation")


def test_time_warp_uses_capability_not_protocol_number(
    flight_state: FlightState, capabilities: CapabilitySet
) -> None:
    runtime = FakeRuntime(flight_state, capabilities)
    result = TimeWarp(runtime).set_rate(2.0)
    assert result.ok and result.result["confirmed_frame"] == 2457


@pytest.mark.parametrize("error_type", [RequestTimeoutError, RuntimeOverloadError])
def test_control_renewal_budget_failure_releases_local_authority(
    flight_state: FlightState, capabilities: CapabilitySet, monkeypatch, error_type,
) -> None:
    runtime = FakeRuntime(flight_state, capabilities)
    control = ControlClient(runtime)
    control.acquire()
    control.set_thrust_vector(Vector3(1.0, 0.0, 0.0))

    def fail_send(*args):
        raise error_type("send budget exhausted")

    monkeypatch.setattr(runtime, "_send_in_session", fail_send)
    control._refresh()
    assert not control.lease_active
    assert control.override is None


def test_control_renewal_keeps_unrelated_errors_visible(
    flight_state: FlightState, capabilities: CapabilitySet, monkeypatch,
) -> None:
    runtime = FakeRuntime(flight_state, capabilities)
    control = ControlClient(runtime)
    control.acquire()
    control.set_thrust_vector(Vector3(1.0, 0.0, 0.0))

    def fail_send(*args):
        raise RuntimeError("unexpected callback error")

    monkeypatch.setattr(runtime, "_send_in_session", fail_send)
    with pytest.raises(RuntimeError, match="unexpected callback error"):
        control._refresh()
