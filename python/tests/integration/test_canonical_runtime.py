from __future__ import annotations

import asyncio
import threading
from dataclasses import replace

from kittenio import Config, ConnectionInfo, FlightState
from kittenio.protocol import Envelope
from kittenio.runtime.engine import KittenRuntime

GAME = "11111111-1111-4111-8111-111111111111"
SESSION = "22222222-2222-4222-8222-222222222222"


def test_control_renewal_runs_independently_of_mission_ticks() -> None:
    class RecordingControl:
        def __init__(self) -> None:
            self.refreshed = threading.Event()

        def _refresh(self) -> None:
            self.refreshed.set()

    runtime = KittenRuntime(Config(pairing_token="a" * 43, command_deadman_ms=250))
    control = RecordingControl()
    runtime._control = control  # type: ignore[assignment]
    runtime._mark_mission_progress()

    worker = threading.Thread(target=runtime._control_renewal_worker)
    worker.start()
    try:
        assert control.refreshed.wait(1.0)
    finally:
        runtime._stopping.set()
        worker.join(1.0)
    assert not worker.is_alive()


def test_runtime_publishes_one_state_and_session_bound_vehicle(
    flight_state: FlightState,
) -> None:
    async def exercise() -> None:
        runtime = KittenRuntime(Config(pairing_token="a" * 43))
        info = ConnectionInfo(True, GAME, SESSION, "game", "mod", "map", 1)
        await runtime._on_connect(info, flight_state.capabilities)
        session = runtime._active_session
        assert session is not None
        envelope = Envelope.create(
            "telemetry",
            game_instance_id=GAME,
            session_id=SESSION,
            seq=1,
            payload={},
        )
        await runtime._on_envelope(envelope, flight_state)
        assert runtime.ksa.data is flight_state
        assert runtime.ksa.capabilities is flight_state.capabilities
        assert runtime.ksa.active_vehicle is not None
        assert runtime.ksa.active_vehicle.id == flight_state.vehicle.id
        assert runtime.ksa.active_vehicle.state is flight_state.vehicle
        assert runtime.ksa.manual_control is flight_state.manual_control
        assert runtime.ksa.state_age_s is not None

        replacement = replace(flight_state, frame_number=flight_state.frame_number + 1)
        await runtime._on_envelope(replace(envelope, seq=2), replacement)
        queued_session, queued_state = runtime._states.get_nowait()
        assert queued_session == session and queued_state is replacement

    asyncio.run(exercise())
