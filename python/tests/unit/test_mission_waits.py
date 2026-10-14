from dataclasses import replace
from types import SimpleNamespace

import pytest
from kittenio import FlightState, WaitTimeoutError, next_tick, wait_real_seconds, wait_sim_seconds, wait_until
from kittenio.runtime import waits
from kittenio.runtime.engine import KittenRuntime
from kittenio.runtime.program import KittenProgram


@pytest.fixture
def clock(monkeypatch):
    now = [100.0]
    monkeypatch.setattr(waits, "time", SimpleNamespace(monotonic=lambda: now[0]))
    return now


def test_simulation_and_real_waits_use_different_clocks(flight_state: FlightState, clock):
    simulation = wait_sim_seconds(2)
    real = wait_real_seconds(2)
    assert not simulation.ready(flight_state)
    assert not real.ready(flight_state)
    clock[0] += 2
    assert real.ready(flight_state)
    assert not simulation.ready(flight_state)
    assert simulation.ready(replace(flight_state, player_time_s=flight_state.player_time_s + 2))


def test_predicate_timeout_starts_when_armed_and_survives_telemetry_gaps(flight_state: FlightState, clock):
    condition = wait_until(lambda state: False, timeout=3, description="touchdown")
    clock[0] += 20
    assert not condition.ready(flight_state)
    clock[0] += 3
    with pytest.raises(WaitTimeoutError, match="after 3s waiting for touchdown"):
        condition._check_timeout()


def test_satisfied_predicate_does_not_expire_before_next_tick(flight_state: FlightState, clock):
    condition = wait_until(lambda state: True, timeout=1)
    assert condition.ready(flight_state)
    clock[0] += 5
    condition._check_timeout()
    assert condition.ready(flight_state)


@pytest.mark.parametrize("timeout", [0, -1, float("inf"), float("nan")])
def test_invalid_predicate_deadlines_are_rejected(timeout):
    with pytest.raises(ValueError):
        wait_until(lambda state: True, timeout=timeout)


def test_predicate_and_description_validation():
    with pytest.raises(TypeError, match="callable"):
        wait_until(None)
    with pytest.raises(ValueError, match="description"):
        wait_until(lambda state: True, description=" ")


@pytest.mark.parametrize("implicit", [False, True])
def test_next_tick_resumes_on_the_next_frame(flight_state: FlightState, implicit):
    events = []

    def script():
        events.append("start")
        yield None if implicit else next_tick()
        events.append("next")

    program = KittenProgram()
    generator = script()
    condition = KittenRuntime._step_script(generator, None, flight_state, program)
    assert events == ["start"]
    assert KittenRuntime._step_script(generator, condition, flight_state, program) is condition
    later = replace(flight_state, frame_number=flight_state.frame_number + 1)
    KittenRuntime._step_script(generator, condition, later, program)
    assert events == ["start", "next"]
    assert program.stopped


def test_simulation_wait_is_armed_at_the_yield_point(flight_state: FlightState):
    def script():
        yield wait_sim_seconds(1)

    program = KittenProgram()
    generator = script()
    condition = KittenRuntime._step_script(generator, None, flight_state, program)
    later = replace(flight_state, player_time_s=flight_state.player_time_s + 1)
    KittenRuntime._step_script(generator, condition, later, program)
    assert program.stopped
