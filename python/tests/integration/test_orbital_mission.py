"""Complete offline trajectory through the actual SDK and WebSocket transport."""

from __future__ import annotations

import threading
import time
from contextlib import contextmanager
from dataclasses import asdict, replace
from pathlib import Path

import pytest
from kittenio import Config, OrbitLaunchError, OrbitLaunchSettings, Vector3, launch_to_orbit, run
from kittenio.api.orbit import _Command
from kittenio.wire.protocol import decode_envelope, encode_envelope
from websockets.exceptions import ConnectionClosed
from websockets.sync.server import serve

from python.tests.support.orbit_simulation import IdealLaunchPlant, prepared_state

FIXTURES = Path(__file__).resolve().parents[3] / "protocol/fixtures/v1"
TOKEN = "test-placeholder-token-" + "x" * 32
LEASE = "33333333-3333-4333-8333-333333333333"


@contextmanager
def orbital_peer(flight_state, *, fuel_failure=False, insufficient_thrust=False, acknowledge_shutdown=True):
    initial = prepared_state(flight_state)
    if insufficient_thrust:
        initial = replace(initial, propulsion=replace(initial.propulsion, maximum_available_thrust_n=100))
    plant = IdealLaunchPlant(initial, attitude_lag_s=0.5, maximum_thrust_n=100 if insufficient_thrust else 2_000_000)
    hello = decode_envelope((FIXTURES / "01-hello.json").read_text())
    sample = decode_envelope((FIXTURES / "03-telemetry.json").read_text())
    hello = replace(hello, payload={**hello.payload, "game_version": "2026.9.7.5402", "starmap_version": "0.4.6"})
    received = []
    failures = []
    stopping = threading.Event()

    def authenticate(connection, request):
        if request.headers.get("Authorization") != f"Bearer {TOKEN}":
            return connection.respond(401, "authentication required")
        return None

    def handler(peer):
        applied = None
        lease_active = False
        try:
            peer.send(encode_envelope(hello))
            welcome = decode_envelope(peer.recv(timeout=2))
            received.append(welcome)
            sequence = 0
            while not stopping.is_set():
                sequence += 1
                state = plant.state()
                payload = dict(sample.payload)
                payload.update(
                    vehicle=asdict(state.vehicle),
                    propulsion=asdict(state.propulsion),
                    surface=asdict(state.surface),
                    player_time_s=state.player_time_s,
                    frame_number=state.frame_number,
                    captured_monotonic_ms=int(time.monotonic() * 1000),
                    applied_control={
                        "lease_active": lease_active,
                        "lease_id": LEASE if lease_active else None,
                        "active_mode": "thrust_vector_target" if applied is not None else None,
                        "latest_requested": applied,
                        "accepted": applied,
                        "applied": applied,
                        "applied_game_frame": state.frame_number,
                        "applied_simulation_time_s": state.player_time_s,
                        "rejection_reason": None,
                        "saturation_reason": None,
                    },
                )
                peer.send(
                    encode_envelope(replace(sample, session_id=welcome.session_id, seq=sequence, payload=payload))
                )
                try:
                    message = decode_envelope(peer.recv(timeout=0.01))
                except TimeoutError:
                    continue
                received.append(message)
                if message.type == "lease.acquire":
                    lease_active = True
                    peer.send(
                        encode_envelope(
                            replace(
                                message,
                                type="lease.granted",
                                seq=1,
                                payload={"lease_id": LEASE, "vehicle_id": initial.vehicle.id, "timeout_ms": 750},
                            )
                        )
                    )
                elif message.type == "lease.release":
                    lease_active = False
                    peer.send(
                        encode_envelope(replace(message, seq=1, payload={"lease_id": LEASE, "reason": "released"}))
                    )
                elif message.type == "ping":
                    peer.send(encode_envelope(replace(message, type="pong")))
                elif message.type == "control":
                    if acknowledge_shutdown or message.payload["engine_on"] is not False:
                        applied = {key: value for key, value in message.payload.items() if key not in {"lease_id"}}
                        applied["monotonic_ms"] = int(time.monotonic() * 1000)
                    vector = message.payload["thrust_vector_target"]["direction_cci"]
                    command = _Command(
                        Vector3(**vector), message.payload["throttle"], message.payload["engine_on"], "test"
                    )
                    plant.step(command, dt=0.5)
                    if fuel_failure and plant.time - initial.player_time_s > 20:
                        plant.mass = plant.dry_mass
        except ConnectionClosed:
            pass
        except BaseException as exc:
            failures.append(exc)

    with serve(handler, "127.0.0.1", 0, process_request=authenticate) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        port = server.socket.getsockname()[1]
        try:
            yield Config(endpoint=f"ws://127.0.0.1:{port}/kittenio/v1", pairing_token=TOKEN), received
        finally:
            stopping.set()
            server.shutdown()
            thread.join(3)
    assert not failures
    assert not thread.is_alive()


def test_launch_to_200_km_and_confirm_engine_cutoff_over_real_transport(flight_state):
    results = []
    phases = []

    def mission(ksa):
        results.append(launch_to_orbit(ksa, on_phase=phases.append))

    with orbital_peer(flight_state) as (config, received):
        run(mission, config=config, connect_timeout=2)
    result = results[0]
    assert abs(result.apoapsis_altitude_m - 200_000) <= 5000
    assert abs(result.periapsis_altitude_m - 200_000) <= 5000
    assert phases == ["ascent", "coast", "circularization", "complete"]
    controls = [message for message in received if message.type == "control"]
    assert controls[-1].payload["engine_on"] is False
    assert controls[-1].payload["throttle"] == 0
    assert [message.type for message in received].count("lease.release") == 1
    assert all(message.type != "request" for message in received)  # No guessed stages or warp changes.


def test_fuel_failure_requests_engine_off_and_releases_lease(flight_state):
    def mission(ksa):
        launch_to_orbit(ksa)

    with orbital_peer(flight_state, fuel_failure=True) as (config, received):
        with pytest.raises(OrbitLaunchError, match="no available engine thrust"):
            run(mission, config=config, connect_timeout=2)
    controls = [message for message in received if message.type == "control"]
    assert controls[-1].payload["engine_on"] is False
    assert controls[-1].payload["throttle"] == 0
    assert [message.type for message in received].count("lease.release") == 1


def test_preflight_failure_never_acquires_or_actuates(flight_state):
    def mission(ksa):
        launch_to_orbit(ksa, OrbitLaunchSettings())

    with orbital_peer(flight_state, insufficient_thrust=True) as (config, received):
        with pytest.raises(OrbitLaunchError, match="thrust-to-weight"):
            run(mission, config=config, connect_timeout=2)
    assert {message.type for message in received} <= {"welcome", "ping", "pong"}


def test_orbit_starter_uses_the_supported_callable_api(tmp_path):
    from kittenio.cli import load_program, main

    path = tmp_path / "orbit.py"
    assert main(["new", str(path), "--template", "orbit"]) == 0
    assert load_program(str(path)).__name__ == "mission"
    assert "TARGET_ALTITUDE_M = 200_000" in path.read_text()


def test_phase_callback_failure_still_confirms_cutoff_and_releases_control(flight_state):
    def fail_phase(phase):
        raise RuntimeError("phase callback failed")

    def mission(ksa):
        launch_to_orbit(ksa, on_phase=fail_phase)

    with orbital_peer(flight_state) as (config, received):
        with pytest.raises(RuntimeError, match="phase callback failed"):
            run(mission, config=config, connect_timeout=2)
    controls = [message for message in received if message.type == "control"]
    assert controls and all(message.payload["engine_on"] is False for message in controls)
    assert [message.type for message in received].count("lease.release") == 1


def test_unconfirmed_cutoff_keeps_the_original_failure_and_records_the_limitation(flight_state):
    def fail_phase(phase):
        raise RuntimeError("phase callback failed")

    def mission(ksa):
        launch_to_orbit(ksa, OrbitLaunchSettings(telemetry_timeout_s=0.1), on_phase=fail_phase)

    with orbital_peer(flight_state, acknowledge_shutdown=False) as (config, received):
        with pytest.raises(RuntimeError, match="phase callback failed") as error:
            run(mission, config=config, connect_timeout=2)
    assert any("could not be confirmed" in note for note in error.value.__notes__)
    assert [message.type for message in received].count("lease.release") == 1
