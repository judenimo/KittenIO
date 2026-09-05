from __future__ import annotations

import json
import math
import threading
import time
from dataclasses import replace
from pathlib import Path

import pytest
from kittenio.identification import (
    acceleration_prediction_errors_mps2,
    command_application_delays_s,
    estimate_effective_isp_s,
    estimate_effective_mass_flow_kgps,
    estimate_ignition_delays_s,
    estimate_spool_time_constants_s,
    estimate_throttle_to_thrust,
    validate_coordinate_conventions,
)
from kittenio.models import FlightState, ModelError, Quaternion, ThrustVectorControl, Vector3
from kittenio.optimizer import LatestOptimizerWorker
from kittenio.protocol import decode_envelope
from kittenio.recording import TelemetryRecorder, TelemetryReplay, export_csv
from kittenio.transforms import (
    as_numpy,
    inverse_rotate_vector,
    normalize_quaternion,
    project_onto_enu,
    rotate_vector,
)

FIXTURE = Path(__file__).resolve().parents[3] / "protocol" / "fixtures" / "v1" / "03-telemetry.json"


def fixture_state() -> FlightState:
    envelope = decode_envelope(FIXTURE.read_text(encoding="utf-8"))
    assert envelope.version == 1
    return FlightState.from_payload(envelope.payload)


def test_golden_fixture_is_strict_and_typed() -> None:
    state = fixture_state()
    assert state.vehicle is not None and state.vehicle.gravity_cci_mps2 == Vector3(-9.8, 0.0, 0.0)
    assert state.propulsion is not None and state.propulsion.engines[0].id == "engine:1"
    assert state.propulsion.maximum_available_thrust_body_n == Vector3(150_000.0, 0.0, 0.0)
    assert state.propulsion.maximum_available_thrust_cci_n == Vector3(150_000.0, 0.0, 0.0)
    assert state.rigid_body is not None and state.rigid_body.total_force_body_n == Vector3(-91_000.0, 0.0, 0.0)
    assert state.rigid_body.aerodynamic_force_body_n is None
    assert state.capabilities.surface.touchdown_events
    assert not state.capabilities.surface.collision_events
    assert state.capabilities.control.set_target
    assert state.capabilities.time_warp.control
    assert state.vehicle.position_orb_m == Vector3(100.0, 0.0, 0.0)
    assert state.vehicle.target_id == "body:moon:2"
    assert state.manual_control is not None and state.manual_control.yaw.value == "positive"
    assert state.landing_reference is not None and state.landing_reference.position_enu_m == Vector3(0.0, 0.0, 100.0)
    assert state.applied_control.applied is not None
    assert state.applied_control.applied_game_frame is None

    payload = json.loads(FIXTURE.read_text(encoding="utf-8"))["payload"]
    payload["invented"] = 0
    with pytest.raises(ModelError, match="unknown"):
        FlightState.from_payload(payload)


def test_control_feedback_and_control_frames_reject_invalid_values() -> None:
    state = fixture_state()
    payload = json.loads(FIXTURE.read_text(encoding="utf-8"))["payload"]
    payload["applied_control"]["applied"]["throttle"] = 1.01
    with pytest.raises(ModelError, match="throttle"):
        FlightState.from_payload(payload)

    with pytest.raises(ModelError, match="unit vector"):
        ThrustVectorControl(Vector3(0.0, 2.0, 0.0))
    assert ThrustVectorControl(Vector3(0.0, 1.0, 0.0), throttle=1.0).to_payload(
        lease_id="33333333-3333-4333-8333-333333333333", control_seq=4
    )["mode"] == "thrust_vector_target"
    assert state.capabilities.control.thrust_vector_target
    assert state.applied_command_age_s == pytest.approx(0.016)


def test_quaternion_rotation_sign_and_enu_conventions() -> None:
    half = math.sqrt(0.5)
    q = Quaternion(0.0, 0.0, half, half)
    rotated = rotate_vector(Vector3(1.0, 0.0, 0.0), q)
    assert rotated.x == pytest.approx(0.0, abs=1e-12)
    assert rotated.y == pytest.approx(1.0)
    assert inverse_rotate_vector(rotated, q).x == pytest.approx(1.0)
    assert normalize_quaternion(Quaternion(0.0, 0.0, -half, -half)) == q
    enu = project_onto_enu(
        Vector3(5.0, 6.0, 7.0),
        Vector3(1.0, 0.0, 0.0),
        Vector3(0.0, 1.0, 0.0),
        Vector3(0.0, 0.0, 1.0),
    )
    assert enu == Vector3(5.0, 6.0, 7.0)
    state = fixture_state()
    assert validate_coordinate_conventions(state) == ()
    assert state.landing_reference is not None
    bad_landing = replace(state.landing_reference, up_cci=state.landing_reference.east_cci)
    problems = validate_coordinate_conventions(replace(state, landing_reference=bad_landing))
    assert "landing ENU basis is not orthogonal" in problems
    assert "landing ENU basis is not right-handed" in problems


def test_numpy_conversion_is_optional_and_typed() -> None:
    np = pytest.importorskip("numpy")
    value = as_numpy(Vector3(1.0, 2.0, 3.0))
    assert value.dtype == np.float64
    assert value.tolist() == [1.0, 2.0, 3.0]


def test_recording_replay_csv_and_identification_use_live_model(tmp_path: Path) -> None:
    state = fixture_state()
    assert state.propulsion is not None
    second_propulsion = replace(
        state.propulsion,
        applied_throttle=0.75,
        current_thrust_body_n=Vector3(10780.0, 0.0, 0.0),
        current_thrust_cci_n=Vector3(10780.0, 0.0, 0.0),
    )
    second = replace(state, frame_number=state.frame_number + 1, propulsion=second_propulsion)
    recording = tmp_path / "flight.jsonl"
    with TelemetryRecorder(recording) as recorder:
        assert recorder.submit(state, received_monotonic_s=10.0)
        assert recorder.submit(second, received_monotonic_s=10.1)

    replayed = tuple(TelemetryReplay(recording))
    assert replayed == (state, second)
    csv_path = tmp_path / "flight.csv"
    assert export_csv(recording, csv_path) == 2
    assert "vertical_velocity_mps" in csv_path.read_text(encoding="utf-8")

    fit = estimate_throttle_to_thrust(replayed)
    assert fit.sample_count == 2 and fit.gain_n == pytest.approx(14000.0)
    assert estimate_effective_isp_s(replayed) > 0
    assert command_application_delays_s(replayed) == pytest.approx((0.001, 0.001))
    errors = acceleration_prediction_errors_mps2(replayed)
    assert len(errors) == 2 and all(math.isfinite(value) for value in errors)


def test_ignition_spool_and_mass_flow_identification() -> None:
    state = fixture_state()
    assert state.vehicle is not None and state.propulsion is not None
    assert state.applied_control.applied is not None
    zero_propulsion = replace(
        state.propulsion,
        applied_throttle=0.0,
        current_thrust_body_n=Vector3(0.0, 0.0, 0.0),
        current_thrust_cci_n=Vector3(0.0, 0.0, 0.0),
        mass_flow_rate_kgps=0.0,
    )
    off_command = replace(state.applied_control.applied, engine_on=False, throttle=0.0, monotonic_ms=1_000)
    off = replace(
        state,
        player_time_s=10.0,
        captured_monotonic_ms=1_000,
        vehicle=replace(state.vehicle, propellant_mass_kg=3_000.0),
        propulsion=zero_propulsion,
        applied_control=replace(state.applied_control, applied=off_command),
    )
    on_command = replace(off_command, engine_on=True, throttle=0.5, monotonic_ms=1_100)
    commanded = replace(
        off,
        frame_number=off.frame_number + 1,
        player_time_s=10.1,
        captured_monotonic_ms=1_100,
        applied_control=replace(off.applied_control, applied=on_command),
    )
    powered_propulsion = replace(
        zero_propulsion,
        applied_throttle=0.5,
        current_thrust_body_n=Vector3(50_000.0, 0.0, 0.0),
        current_thrust_cci_n=Vector3(50_000.0, 0.0, 0.0),
        mass_flow_rate_kgps=5.0,
    )
    powered = replace(
        commanded,
        frame_number=commanded.frame_number + 1,
        player_time_s=10.15,
        captured_monotonic_ms=1_150,
        vehicle=replace(state.vehicle, propellant_mass_kg=2_999.75),
        propulsion=powered_propulsion,
    )
    samples = (off, commanded, powered)
    assert estimate_ignition_delays_s(samples) == pytest.approx((0.05,))
    assert estimate_spool_time_constants_s(samples) == pytest.approx((0.05,))
    assert estimate_effective_mass_flow_kgps(samples) == pytest.approx(5.0)


def test_optimizer_worker_warmup_capacity_one_and_stale_plan() -> None:
    first_started = threading.Event()
    release_first = threading.Event()
    warmed = threading.Event()

    def solve(value: int) -> int:
        if value == 1:
            first_started.set()
            assert release_first.wait(1.0)
        return value * 10

    with LatestOptimizerWorker(solve, warmup=warmed.set) as worker:
        deadline = time.monotonic() + 1.0
        while not warmed.is_set() and time.monotonic() < deadline:
            time.sleep(0.001)
        worker.submit(1, frame_number=1, simulation_time_s=1.0)
        assert first_started.wait(1.0)
        worker.submit(2, frame_number=2, simulation_time_s=2.0)
        worker.submit(3, frame_number=3, simulation_time_s=3.0)
        release_first.set()
        deadline = time.monotonic() + 1.0
        while (worker.latest_plan() is None or worker.latest_plan().source_frame != 3) and time.monotonic() < deadline:
            time.sleep(0.001)
        plan = worker.latest_plan()
        assert plan is not None and plan.value == 30 and plan.source_frame == 3
        assert plan.is_usable(max_age_s=1.0, current_simulation_time_s=3.1, max_source_age_s=0.2)
        assert not plan.is_usable(max_age_s=1.0, current_simulation_time_s=4.0, max_source_age_s=0.2)


def test_optimizer_failure_is_reported_without_crossing_callback_thread() -> None:
    with LatestOptimizerWorker(lambda _: (_ for _ in ()).throw(RuntimeError("infeasible"))) as worker:
        worker.submit(object(), frame_number=9, simulation_time_s=4.0)
        deadline = time.monotonic() + 1.0
        while worker.latest_plan() is None and time.monotonic() < deadline:
            time.sleep(0.001)
        plan = worker.latest_plan()
        assert plan is not None and not plan.feasible and plan.error == "infeasible"
        assert not worker.healthy

def test_optimizer_solve_deadline_marks_late_plan_unusable() -> None:
    with pytest.raises(ValueError, match="finite and positive"):
        LatestOptimizerWorker(lambda value: value, solve_deadline_s=0.0)

    release = threading.Event()
    started = threading.Event()

    def slow_solve(value: int) -> int:
        started.set()
        assert release.wait(1.0)
        return value

    with LatestOptimizerWorker(slow_solve, solve_deadline_s=0.01) as worker:
        worker.submit(7, frame_number=7, simulation_time_s=7.0)
        assert started.wait(1.0)
        time.sleep(0.02)
        release.set()
        deadline = time.monotonic() + 1.0
        while worker.latest_plan() is None and time.monotonic() < deadline:
            time.sleep(0.001)
        plan = worker.latest_plan()
        assert plan is not None
        assert plan.deadline_exceeded(0.01)
        assert plan.solve_duration_s > 0.01
        assert not plan.feasible and plan.value is None
        assert plan.error is not None and plan.error.startswith("solve deadline exceeded")
        assert not plan.is_usable(
            max_age_s=1.0,
            current_simulation_time_s=7.0,
            max_source_age_s=1.0,
        )
        assert not worker.healthy
