"""Generator-friendly mission waits."""

from __future__ import annotations

import math
import time
from dataclasses import dataclass
from typing import Callable, Protocol

from ..wire.models import FlightState


class WaitCondition(Protocol):
    def ready(self, state: FlightState) -> bool: ...


class WaitTimeoutError(TimeoutError):
    """A predicate did not become true within its real-time deadline."""


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
    timeout_s: float | None = None
    description: str = "condition"
    deadline_s: float | None = None
    completed: bool = False

    def ready(self, state: FlightState) -> bool:
        if self.completed:
            return True
        if self.deadline_s is None and self.timeout_s is not None:
            self.deadline_s = time.monotonic() + self.timeout_s
        self._check_timeout()
        self.completed = bool(self.predicate(state))
        return self.completed

    def _check_timeout(self) -> None:
        if not self.completed and self.deadline_s is not None and time.monotonic() >= self.deadline_s:
            raise WaitTimeoutError(f"timed out after {self.timeout_s:g}s waiting for {self.description}")


def _duration(value: float, name: str) -> float:
    result = float(value)
    if not math.isfinite(result) or result < 0:
        raise ValueError(f"{name} must be finite and non-negative")
    return result


def next_tick() -> WaitCondition:
    """Resume at the next distinct telemetry frame."""
    return _NextTick()


def wait_sim_seconds(seconds: float) -> WaitCondition:
    """Wait using KSA's simulation clock; this wait pauses when the game pauses."""
    return _WaitSimSeconds(_duration(seconds, "seconds"))


def wait_real_seconds(seconds: float) -> WaitCondition:
    """Resume on a telemetry frame after this many wall-clock seconds."""
    return _WaitRealSeconds(_duration(seconds, "seconds"))


def wait_until(
    predicate: Callable[[FlightState], bool],
    *,
    timeout: float | None = None,
    description: str = "condition",
) -> WaitCondition:
    """Wait for a telemetry predicate, optionally bounded by real seconds.

    The timeout starts when the mission yields this condition and continues
    during game pauses or telemetry gaps. Predicates run on the mission thread.
    """
    if not callable(predicate):
        raise TypeError("predicate must be callable")
    timeout_s = None if timeout is None else _duration(timeout, "timeout")
    if timeout_s == 0:
        raise ValueError("timeout must be greater than zero")
    if not isinstance(description, str) or not description.strip():
        raise ValueError("description must be a non-empty string")
    return _WaitUntil(predicate, timeout_s, description)
