"""Capacity-one optimizer worker primitives for non-blocking MPC/SCvx loops."""

from __future__ import annotations

import math
import threading
import time
from dataclasses import dataclass
from typing import Callable, Generic, TypeVar

StateT = TypeVar("StateT")
PlanT = TypeVar("PlanT")


@dataclass(frozen=True, slots=True)
class SolverPlan(Generic[PlanT]):
    value: PlanT | None
    source_frame: int
    source_simulation_time_s: float
    started_monotonic_s: float
    finished_monotonic_s: float
    feasible: bool
    error: str | None = None

    def age_s(self, now_s: float | None = None) -> float:
        return (time.monotonic() if now_s is None else now_s) - self.finished_monotonic_s

    def source_age_s(self, current_simulation_time_s: float) -> float:
        return current_simulation_time_s - self.source_simulation_time_s

    @property
    def solve_duration_s(self) -> float:
        return max(0.0, self.finished_monotonic_s - self.started_monotonic_s)

    def deadline_exceeded(self, deadline_s: float) -> bool:
        if not math.isfinite(deadline_s) or deadline_s <= 0:
            raise ValueError("deadline_s must be finite and positive")
        return self.solve_duration_s > deadline_s

    def is_usable(self, *, max_age_s: float, current_simulation_time_s: float, max_source_age_s: float) -> bool:
        return (
            self.feasible
            and self.error is None
            and self.age_s() <= max_age_s
            and self.source_age_s(current_simulation_time_s) <= max_source_age_s
        )


@dataclass(frozen=True, slots=True)
class OptimizerInput(Generic[StateT]):
    state: StateT
    frame_number: int
    simulation_time_s: float


class LatestOptimizerWorker(Generic[StateT, PlanT]):
    """Run expensive solves off-thread with one latest-state and one latest-plan slot."""

    def __init__(
        self,
        solve: Callable[[StateT], PlanT],
        *,
        warmup: Callable[[], None] | None = None,
        solve_deadline_s: float | None = None,
    ) -> None:
        if solve_deadline_s is not None and (not math.isfinite(solve_deadline_s) or solve_deadline_s <= 0):
            raise ValueError("solve_deadline_s must be finite and positive")
        self._solve = solve
        self._warmup = warmup
        self._solve_deadline_s = solve_deadline_s
        self._condition = threading.Condition()
        self._latest_input: OptimizerInput[StateT] | None = None
        self._latest_plan: SolverPlan[PlanT] | None = None
        self._stopping = False
        self._busy = False
        self._thread: threading.Thread | None = None

    def start(self) -> None:
        with self._condition:
            if self._thread is not None:
                return
            self._thread = threading.Thread(target=self._run, name="kittenio-optimizer", daemon=True)
            self._thread.start()

    def submit(self, state: StateT, *, frame_number: int, simulation_time_s: float) -> None:
        with self._condition:
            if self._stopping:
                raise RuntimeError("optimizer worker is stopping")
            self._latest_input = OptimizerInput(state, frame_number, simulation_time_s)
            self._condition.notify()

    def latest_plan(self) -> SolverPlan[PlanT] | None:
        with self._condition:
            return self._latest_plan

    @property
    def healthy(self) -> bool:
        with self._condition:
            return not self._stopping and (self._latest_plan is None or self._latest_plan.error is None)

    @property
    def busy(self) -> bool:
        with self._condition:
            return self._busy

    def close(self, timeout: float = 2.0) -> None:
        with self._condition:
            self._stopping = True
            self._condition.notify_all()
            thread = self._thread
        if thread is not None:
            thread.join(timeout)

    def __enter__(self) -> "LatestOptimizerWorker[StateT, PlanT]":
        self.start()
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def _run(self) -> None:
        try:
            if self._warmup is not None:
                self._warmup()
        except Exception as exc:
            now = time.monotonic()
            with self._condition:
                self._latest_plan = SolverPlan(None, -1, 0.0, now, now, False, f"warmup failed: {exc}")
                self._stopping = True
                return
        while True:
            with self._condition:
                while self._latest_input is None and not self._stopping:
                    self._condition.wait()
                if self._stopping:
                    return
                item = self._latest_input
                self._latest_input = None
                self._busy = True
            assert item is not None
            started = time.monotonic()
            try:
                value = self._solve(item.state)
                finished = time.monotonic()
                if self._solve_deadline_s is not None and finished - started > self._solve_deadline_s:
                    plan = SolverPlan(
                        None,
                        item.frame_number,
                        item.simulation_time_s,
                        started,
                        finished,
                        False,
                        f"solve deadline exceeded ({finished - started:.6f}s > {self._solve_deadline_s:.6f}s)",
                    )
                else:
                    plan = SolverPlan(value, item.frame_number, item.simulation_time_s, started, finished, True)
            except Exception as exc:
                plan = SolverPlan(
                    None, item.frame_number, item.simulation_time_s, started, time.monotonic(), False, str(exc)
                )
            with self._condition:
                self._latest_plan = plan
                self._busy = False
