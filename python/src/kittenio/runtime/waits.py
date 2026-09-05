"""Generator-friendly mission waits."""

from __future__ import annotations

import math
import time
from dataclasses import dataclass
from typing import Callable, Protocol

from ..wire.models import FlightState


class WaitCondition(Protocol):
    def ready(self, state: FlightState) -> bool: ...


@dataclass(slots=True)
class _NextTick:
    initial_frame: int | None = None

    def ready(self, state: FlightState) -> bool:
        if self.initial_frame is None:
            self.initial_frame = state.frame_number
            return False
        return state.frame_number != self.initial_frame


@dataclass(slots=True)
class _WaitSimSeconds:
    duration_s: float
    deadline_s: float | None = None

    def ready(self, state: FlightState) -> bool:
        if self.deadline_s is None:
            self.deadline_s = state.player_time_s + self.duration_s
        return state.player_time_s >= self.deadline_s


@dataclass(slots=True)
class _WaitRealSeconds:
    duration_s: float
    deadline_s: float | None = None

    def ready(self, state: FlightState) -> bool:
        del state
        if self.deadline_s is None:
            self.deadline_s = time.monotonic() + self.duration_s
        return time.monotonic() >= self.deadline_s


@dataclass(slots=True)
class _WaitUntil:
    predicate: Callable[[FlightState], bool]

    def ready(self, state: FlightState) -> bool:
        return bool(self.predicate(state))


def _duration(value: float, name: str) -> float:
    result = float(value)
    if not math.isfinite(result) or result < 0:
        raise ValueError(f"{name} must be finite and non-negative")
    return result


def next_tick() -> WaitCondition:
    return _NextTick()


def wait_sim_seconds(seconds: float) -> WaitCondition:
    return _WaitSimSeconds(_duration(seconds, "seconds"))


def wait_real_seconds(seconds: float) -> WaitCondition:
    return _WaitRealSeconds(_duration(seconds, "seconds"))


def wait_until(predicate: Callable[[FlightState], bool]) -> WaitCondition:
    if not callable(predicate):
        raise TypeError("predicate must be callable")
    return _WaitUntil(predicate)
