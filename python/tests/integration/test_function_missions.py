from __future__ import annotations

import inspect
import json
import threading
import time
from contextlib import contextmanager
from dataclasses import replace
from pathlib import Path

import pytest
from kittenio import (
    Config,
    ConnectionTimeoutError,
    ControlError,
    KittenProgram,
    KsaApi,
    MissionInterruptedError,
    WaitTimeoutError,
    next_tick,
    run,
    wait_sim_seconds,
    wait_until,
)
from kittenio.runtime.engine import KittenRuntime, RuntimeErrorBase
from kittenio.wire.protocol import decode_envelope, encode_envelope
from websockets.exceptions import ConnectionClosed
from websockets.sync.server import serve

FIXTURES = Path(__file__).resolve().parents[3] / "protocol" / "fixtures" / "v1"
TEST_TOKEN = "test-placeholder-token-" + "x" * 32


@contextmanager
def game_peer(
    *,
    disconnect_after: threading.Event | None = None,
    telemetry: str = "stream",
    orientation: dict[str, float] | None = None,
    no_orientation: bool = False,
    simulation_step: float = 0.05,
):
    """Exercise the real SDK transport against an authenticated local game peer."""
    received = []
    failures = []
    stopping = threading.Event()
    hello = decode_envelope((FIXTURES / "01-hello.json").read_text(encoding="utf-8"))
    hello = replace(hello, payload={**hello.payload, "game_version": "2026.9.7.5402", "starmap_version": "0.4.6"})
    sample = decode_envelope((FIXTURES / "03-telemetry.json").read_text(encoding="utf-8"))

    def authenticate(connection, request):
        if request.headers.get("Authorization") != f"Bearer {TEST_TOKEN}":
            return connection.respond(401, "authentication required")
        return None

    def handler(peer):
        try:
            peer.send(encode_envelope(hello))
            welcome = decode_envelope(peer.recv(timeout=2))
            received.append(welcome)
            frame = 0
            while not stopping.is_set():
                if disconnect_after is not None and disconnect_after.is_set():
                    peer.close()
                    return
                if telemetry == "stream" or (telemetry == "once" and frame == 0):
                    frame += 1
                    payload = dict(sample.payload)
                    if orientation is not None or no_orientation:
                        payload["vehicle"] = {**payload["vehicle"], "body_to_cci": orientation}
                    payload["frame_number"] += frame
                    payload["player_time_s"] += frame * simulation_step
                    envelope = replace(sample, seq=frame, session_id=welcome.session_id, payload=payload)
                    peer.send(encode_envelope(envelope))
                try:
                    message = decode_envelope(peer.recv(timeout=0.01))
                except TimeoutError:
                    continue
                received.append(message)
                if message.type == "ping":
                    peer.send(encode_envelope(replace(message, type="pong")))
                elif message.type == "lease.acquire":
                    peer.send(
                        encode_envelope(
                            replace(
                                message,
                                type="lease.granted",
                                seq=1,
                                payload={
                                    "lease_id": "33333333-3333-4333-8333-333333333333",
                                    "vehicle_id": message.payload["vehicle_id"],
                                    "timeout_ms": 750,
                                },
                            )
                        )
                    )
                elif message.type == "lease.release":
                    peer.send(
                        encode_envelope(
                            replace(
                                message,
                                seq=1,
                                payload={**message.payload, "reason": "released"},
                            )
                        )
                    )
        except ConnectionClosed:
            pass
        except BaseException as exc:
            failures.append(exc)

    with serve(handler, "127.0.0.1", 0, process_request=authenticate) as server:
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        port = server.socket.getsockname()[1]
        config = Config(endpoint=f"ws://127.0.0.1:{port}/kittenio/v1", pairing_token=TEST_TOKEN)
        try:
            yield config, received
        finally:
            stopping.set()
            server.shutdown()
            worker.join(3)
    assert not failures
    assert not worker.is_alive()


def test_function_runs_after_telemetry_and_finishes_without_actuation():
    frames = []

    def mission(ksa):
        assert ksa.connection.connected
        assert ksa.active_vehicle is not None
        frames.append(ksa.data.frame_number)
        yield next_tick()
        frames.append(ksa.data.frame_number)

    with game_peer() as (config, received):
        run(mission, config=config, connect_timeout=2)
    assert len(frames) == 2 and frames[1] > frames[0]
    assert {message.type for message in received} <= {"welcome", "ping", "pong"}


def test_regular_function_can_read_once_and_return():
    names = []

    def mission(ksa):
        names.append(ksa.active_vehicle.name)

    with game_peer() as (config, _):
        run(mission, config=config, connect_timeout=2)
    assert len(names) == 1


def test_disconnect_closes_mission_without_replaying_it():
    started = threading.Event()
    events = []

    def mission(ksa):
        events.append("start")
        started.set()
        try:
            yield wait_until(lambda state: False)
        finally:
            events.append("cleanup")

    with game_peer(disconnect_after=started) as (config, _):
        with pytest.raises(MissionInterruptedError, match="rerun the mission explicitly"):
            run(mission, config=config, connect_timeout=2)
    assert events == ["start", "cleanup"]


def test_timeout_during_a_telemetry_gap_runs_finally():
    events = []

    def mission(ksa):
        try:
            yield wait_until(lambda state: False, timeout=0.02, description="touchdown")
        finally:
            events.append("cleanup")

    with game_peer(telemetry="once") as (config, _):
        with pytest.raises(WaitTimeoutError, match="touchdown"):
            run(mission, config=config, connect_timeout=2)
    assert events == ["cleanup"]


def test_first_telemetry_timeout_closes_network_threads(monkeypatch):
    instances = []
    original_init = KittenRuntime.__init__

    def capture(self, config):
        original_init(self, config)
        instances.append(self)

    monkeypatch.setattr(KittenRuntime, "__init__", capture)
    with game_peer(telemetry="none") as (config, _):
        with pytest.raises(ConnectionTimeoutError, match="load a flight"):
            run(lambda ksa: None, config=config, connect_timeout=0.1)
    assert not instances[0]._thread.is_alive()
    assert not instances[0]._control_renewal_thread.is_alive()


@pytest.mark.parametrize("telemetry", ["stream", "once"])
def test_missions_can_catch_wait_timeouts_at_the_yield_expression(telemetry):
    events = []

    def mission(ksa):
        try:
            yield wait_until(lambda state: False, timeout=0.02)
        except WaitTimeoutError:
            events.append("handled")
            yield next_tick()
        events.append("finished")

    def gap_mission(ksa):
        try:
            yield wait_until(lambda state: False, timeout=0.02)
        except WaitTimeoutError:
            events.extend(["handled", "finished"])

    with game_peer(telemetry=telemetry) as (config, _):
        run(gap_mission if telemetry == "once" else mission, config=config, connect_timeout=2)
    assert events == ["handled", "finished"]


def test_interrupt_closes_a_suspended_class_script():
    events = []

    class Program(KittenProgram):
        def on_tick(self, state):
            if events:
                raise KeyboardInterrupt

        def script(self):
            try:
                events.append("start")
                yield wait_until(lambda state: False)
            finally:
                events.append("cleanup")

    with game_peer() as (config, _):
        with pytest.raises(KeyboardInterrupt):
            run(Program, config=config, connect_timeout=2)
    assert events == ["start", "cleanup"]


def test_keyboard_interrupt_closes_the_generator():
    events = []

    def mission(ksa):
        try:
            yield next_tick()
            raise KeyboardInterrupt
        finally:
            events.append("cleanup")

    with game_peer() as (config, _):
        with pytest.raises(KeyboardInterrupt):
            run(mission, config=config, connect_timeout=2)
    assert events == ["cleanup"]


def test_established_callback_programs_remain_supported():
    frames = []

    class Program(KittenProgram):
        def on_tick(self, state):
            frames.append(state.frame_number)
            self.stop()

    with game_peer() as (config, _):
        run(Program, config=config, connect_timeout=2)
    assert len(frames) == 1


@pytest.mark.parametrize("value", [0, -1, float("inf"), float("nan"), True])
def test_invalid_connection_deadline_fails_before_configuration_is_loaded(value):
    with pytest.raises((TypeError, ValueError), match="timeout"):
        run(lambda ksa: None, connect_timeout=value)


def test_starter_mission_runs_on_real_transport_without_commands(tmp_path, monkeypatch, capsys):
    from kittenio.cli import load_program, main

    path = tmp_path / "mission.py"
    assert main(["new", str(path)]) == 0
    # Keep all ten samples while making the starter quick enough for a test.
    original_wait = KsaApi.wait_real_seconds
    monkeypatch.setattr(KsaApi, "wait_real_seconds", lambda self, seconds: original_wait(self, 0))
    with game_peer() as (config, received):
        mission = load_program(str(path))
        assert not inspect.isgeneratorfunction(mission)
        run(mission, config=config, connect_timeout=2)
    output = capsys.readouterr().out
    assert output.count("radar altitude") == 10
    assert "Mission complete." in output
    assert {message.type for message in received} <= {"welcome", "ping", "pong"}


def test_cli_runs_function_file_end_to_end(tmp_path, capsys):
    from kittenio.cli import main

    path = tmp_path / "flight mission.py"
    path.write_text(
        "from kittenio import next_tick\n"
        "def mission(ksa):\n"
        "    print('Connected to', ksa.active_vehicle.name)\n"
        "    yield next_tick()\n"
        "    print('Finished')\n",
        encoding="utf-8",
    )
    with game_peer() as (config, _):
        config_path = tmp_path / "kittenio.json"
        config_path.write_text(json.dumps(config.to_dict()), encoding="utf-8")
        assert main([str(path), "--config", str(config_path), "--connect-timeout", "2"]) == 0
    output = capsys.readouterr().out
    assert "Connected to" in output and "Finished" in output


def test_current_direction_helper_uses_orientation_and_releases_on_timeout():
    def mission(ksa):
        with ksa.active_vehicle.control.leased() as control:
            control.hold_current_direction(engine_on=True, throttle=0.4)
            control.set_throttle(0.2)
            yield wait_until(lambda state: False, timeout=0.02)

    # A half turn around Z maps body +X to inertial -X.
    with game_peer(orientation={"x": 0.0, "y": 0.0, "z": 1.0, "w": 0.0}) as (config, received):
        with pytest.raises(WaitTimeoutError):
            run(mission, config=config, connect_timeout=2)
    controls = [message for message in received if message.type == "control"]
    assert controls[0].payload["thrust_vector_target"]["direction_cci"] == {"x": -1.0, "y": 0.0, "z": 0.0}
    assert controls[0].payload["engine_on"] is True
    assert controls[0].payload["throttle"] == 0.4
    assert controls[-1].payload["throttle"] == 0.2
    assert [message.type for message in received].count("lease.release") == 1


def test_current_direction_helper_requires_lease():
    def mission(ksa):
        ksa.active_vehicle.control.hold_current_direction(throttle=0.2)

    with game_peer() as (config, received):
        with pytest.raises(ControlError, match="lease"):
            run(mission, config=config, connect_timeout=2)
    assert all(message.type != "control" for message in received)


def test_current_direction_helper_rejects_missing_orientation():
    def mission(ksa):
        with ksa.active_vehicle.control.leased() as control:
            control.hold_current_direction(throttle=0.2)

    with game_peer(no_orientation=True) as (config, received):
        with pytest.raises(ControlError, match="orientation is unavailable"):
            run(mission, config=config, connect_timeout=2)
    assert all(message.type != "control" for message in received)
    assert any(message.type == "lease.release" for message in received)


def test_ordinary_mission_wait_calls_return_fresh_telemetry():
    snapshots = []

    def mission(ksa):
        first = ksa.data
        after_tick = ksa.next_tick()
        assert after_tick.frame_number > first.frame_number
        after_sim_wait = ksa.wait(0.2)
        assert after_sim_wait.player_time_s >= after_tick.player_time_s + 0.2
        started = time.monotonic()
        after_real_wait = ksa.wait_real_seconds(0.02)
        assert time.monotonic() - started >= 0.02
        target = after_real_wait.frame_number + 3
        after_condition = ksa.wait_until(lambda state: state.frame_number >= target, timeout=2)
        assert after_condition.frame_number >= target
        snapshots.extend([first, after_tick, after_sim_wait, after_real_wait, after_condition])

    assert not inspect.isgeneratorfunction(mission)
    with game_peer() as (config, received):
        run(mission, config=config, connect_timeout=2)
    assert len(snapshots) == 5
    assert {message.type for message in received} <= {"welcome", "ping", "pong"}


def test_direct_real_wait_continues_while_simulation_is_paused():
    snapshots = []

    def mission(ksa):
        before = ksa.data
        after = ksa.wait_real_seconds(0.03)
        assert after.player_time_s == before.player_time_s
        assert after.frame_number > before.frame_number
        snapshots.append(after)

    with game_peer(simulation_step=0) as (config, _):
        run(mission, config=config, connect_timeout=2)
    assert snapshots


@pytest.mark.parametrize("telemetry", ["stream", "once"])
def test_direct_predicate_timeout_is_catchable_and_wait_context_recovers(telemetry):
    events = []

    def mission(ksa):
        try:
            ksa.wait_until(lambda state: False, timeout=0.02, description="touchdown")
        except WaitTimeoutError as exc:
            assert "touchdown" in str(exc)
            events.append("handled")
        # A second call proves the first wait released its nesting guard.
        assert ksa.wait(0).vehicle is not None
        events.append("finished")

    with game_peer(telemetry=telemetry) as (config, _):
        run(mission, config=config, connect_timeout=2)
    assert events == ["handled", "finished"]


def test_direct_wait_stops_on_disconnect_and_runs_finally():
    started = threading.Event()
    events = []

    def mission(ksa):
        events.append("start")
        started.set()
        try:
            ksa.wait(3600)
            events.append("continued")
        finally:
            events.append("cleanup")

    with game_peer(disconnect_after=started) as (config, _):
        with pytest.raises(MissionInterruptedError, match="rerun the mission explicitly"):
            run(mission, config=config, connect_timeout=2)
    assert events == ["start", "cleanup"]


def test_direct_wait_keeps_control_renewing_beyond_the_mission_progress_grace():
    def mission(ksa):
        with ksa.active_vehicle.control.leased() as control:
            control.hold_current_direction(engine_on=True, throttle=0.2)
            ksa.wait_real_seconds(2.4)

    with game_peer() as (config, received):
        run(mission, config=config, connect_timeout=2)
    controls = [message for message in received if message.type == "control"]
    assert controls[-1].sent_monotonic_ms - controls[0].sent_monotonic_ms >= 2000
    assert all(message.payload["throttle"] == 0.2 for message in controls)
    assert [message.type for message in received].count("lease.release") == 1


def test_direct_wait_timeout_and_interrupt_release_control():
    events = []

    def mission(ksa):
        with ksa.active_vehicle.control.leased() as control:
            control.hold_current_direction(throttle=0.2)
            try:
                ksa.wait_until(lambda state: False, timeout=0.02)
            except WaitTimeoutError:
                events.append("timeout")

            def interrupted(state):
                raise KeyboardInterrupt

            ksa.wait_until(interrupted)

    with game_peer() as (config, received):
        with pytest.raises(KeyboardInterrupt):
            run(mission, config=config, connect_timeout=2)
    assert events == ["timeout"]
    assert [message.type for message in received].count("lease.release") == 1


def test_direct_wait_rejects_nested_predicates_and_foreign_threads():
    errors = []

    def mission(ksa):
        with pytest.raises(RuntimeErrorBase, match="inside a wait predicate"):
            ksa.wait_until(lambda state: ksa.wait(0))

        def worker():
            try:
                ksa.wait(0)
            except RuntimeErrorBase as exc:
                errors.append(str(exc))

        thread = threading.Thread(target=worker)
        thread.start()
        thread.join(1)
        assert not thread.is_alive()
        assert ksa.wait(0).vehicle is not None

    with game_peer() as (config, _):
        run(mission, config=config, connect_timeout=2)
    assert len(errors) == 1 and "mission thread" in errors[0]


def test_callback_programs_cannot_reenter_the_runtime_with_a_direct_wait():
    class Program(KittenProgram):
        def on_tick(self, state):
            self.ksa.wait(0)

    with game_peer() as (config, _):
        with pytest.raises(RuntimeErrorBase, match="inside mission"):
            run(Program, config=config, connect_timeout=2)


def test_helper_functions_can_use_ordinary_wait_calls():
    events = []

    def helper(ksa):
        before = ksa.data.frame_number
        after = ksa.next_tick()
        assert after.frame_number > before
        events.append("helper")

    def mission(ksa):
        helper(ksa)
        events.append("mission")

    with game_peer() as (config, _):
        run(mission, config=config, connect_timeout=2)
    assert events == ["helper", "mission"]


def test_direct_wait_does_not_extend_control_renewal_during_a_telemetry_outage(monkeypatch):
    monkeypatch.setattr("kittenio.runtime.engine._CONTROL_RENEWAL_GRACE_S", 0.15)
    monkeypatch.setattr("kittenio.runtime.engine._MAX_CONTROL_RENEWAL_INTERVAL_S", 0.05)

    def mission(ksa):
        with ksa.active_vehicle.control.leased() as control:
            control.hold_current_direction(throttle=0.2)
            ksa.wait_until(lambda state: False, timeout=0.5)

    with game_peer(telemetry="once") as (config, received):
        with pytest.raises(WaitTimeoutError):
            run(mission, config=config, connect_timeout=2)
    controls = [message for message in received if message.type == "control"]
    assert controls[-1].sent_monotonic_ms - controls[0].sent_monotonic_ms < 350
    assert [message.type for message in received].count("lease.release") == 1


def test_direct_wait_can_be_followed_by_a_generator_wait_without_arming_a_stale_clock():
    elapsed = []

    def mission(ksa):
        initial = ksa.data.player_time_s
        ksa.wait(0.3)
        yield wait_sim_seconds(0.3)
        elapsed.append(ksa.data.player_time_s - initial)

    with game_peer() as (config, _):
        run(mission, config=config, connect_timeout=2)
    assert elapsed[0] >= 0.6


def test_external_runtime_shutdown_interrupts_a_direct_wait(monkeypatch):
    instances = []
    workers = []
    events = []
    original_init = KittenRuntime.__init__

    def capture(self, config):
        original_init(self, config)
        instances.append(self)

    monkeypatch.setattr(KittenRuntime, "__init__", capture)

    def mission(ksa):
        thread = threading.Thread(target=instances[0].stop)
        workers.append(thread)
        thread.start()
        try:
            ksa.wait(3600)
            events.append("continued")
        finally:
            events.append("cleanup")

    try:
        with game_peer() as (config, _):
            with pytest.raises(MissionInterruptedError):
                run(mission, config=config, connect_timeout=2)
    finally:
        for worker in workers:
            worker.join(2)
    assert events == ["cleanup"]
    assert all(not worker.is_alive() for worker in workers)
    assert instances[0]._running_program is None
    assert instances[0]._mission_thread_id is None
