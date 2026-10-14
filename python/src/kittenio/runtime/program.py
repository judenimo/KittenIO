"""Public flight-program base class and runner."""

from __future__ import annotations

import inspect
from collections.abc import Callable, Iterable
from pathlib import Path
from typing import TYPE_CHECKING, TypeVar

from ..transport.config import Config, load_config
from ..wire.common import ConnectionInfo
from ..wire.models import FlightState

if TYPE_CHECKING:
    from .engine import KittenRuntime, KsaApi
    from .waits import WaitCondition


class KittenProgram:
    """Base class for Python flight software.

    Override callbacks or :meth:`script`. User code always runs on one thread;
    WebSocket I/O continues independently on the SDK network thread.
    """

    def __init__(self) -> None:
        self._runtime: KittenRuntime | None = None
        self._stopped = False

    @property
    def ksa(self) -> "KsaApi":
        if self._runtime is None:
            raise RuntimeError("program has not been bound to a KittenIO runtime")
        return self._runtime.ksa

    @property
    def stopped(self) -> bool:
        return self._stopped

    def stop(self) -> None:
        self._stopped = True

    def on_connect(self, connection: ConnectionInfo) -> None:
        pass

    def on_start(self) -> None:
        pass

    def on_tick(self, state: FlightState) -> None:
        pass

    def on_vehicle_changed(self, previous_id: str | None, current_id: str | None) -> None:
        pass

    def on_disconnect(self, connection: ConnectionInfo, reason: str) -> None:
        pass

    def on_stop(self) -> None:
        pass

    def script(self) -> Iterable["WaitCondition"] | None:
        return None

    def _bind(self, runtime: "KittenRuntime") -> None:
        if self._runtime is not None:
            raise RuntimeError("a KittenProgram instance can only be run once")
        self._runtime = runtime


ProgramT = TypeVar("ProgramT", bound=KittenProgram)
Mission = Callable[["KsaApi"], Iterable["WaitCondition"] | None]


class MissionInterruptedError(RuntimeError):
    """A function mission lost its connection and must be restarted explicitly."""


def _validate_mission(mission: Mission) -> None:
    if not callable(mission) or inspect.iscoroutinefunction(mission) or inspect.isasyncgenfunction(mission):
        raise TypeError("mission must be a synchronous function accepting one ksa argument; use ksa.wait() for waits")
    try:
        inspect.signature(mission).bind(None)
    except (TypeError, ValueError) as exc:
        raise TypeError("mission must accept one ksa argument, for example: def mission(ksa): ...") from exc


class _FunctionProgram(KittenProgram):
    def __init__(self, mission: Mission) -> None:
        super().__init__()
        _validate_mission(mission)
        self._mission = mission
        self._mission_started = False
        self.interrupted = False

    def script(self) -> Iterable["WaitCondition"]:
        self._mission_started = True
        produced = self._mission(self.ksa)
        if produced is not None:
            if not isinstance(produced, Iterable):
                raise TypeError("mission must return None or yield KittenIO wait conditions")
            yield from produced

    def on_disconnect(self, connection: ConnectionInfo, reason: str) -> None:
        if self._mission_started:
            self.interrupted = True
            self.stop()


def run(
    program: type[ProgramT] | ProgramT | Mission,
    *,
    config: Config | None = None,
    config_path: str | Path | None = None,
    connect_timeout: float = 15.0,
) -> None:
    """Run a function mission or a program until completion or interruption.

    Function missions receive ``ksa`` after the first telemetry frame. Ordinary
    calls such as ``ksa.wait(2)`` cooperate with the runtime automatically.
    Generator missions may also yield wait conditions. Both stop when they
    return. A lost connection stops a
    function mission; it is never replayed automatically after reconnection.
    ``connect_timeout`` bounds the wait for the first telemetry in real seconds.
    """

    from .engine import KittenRuntime

    if isinstance(program, type) and issubclass(program, KittenProgram):
        instance = program()
    elif isinstance(program, KittenProgram):
        instance = program
    elif callable(program) and not isinstance(program, type):
        instance = _FunctionProgram(program)
    else:
        raise TypeError("program must be a mission function or a KittenProgram class or instance")
    KittenRuntime._deadline(connect_timeout, "connect_timeout")
    resolved = config or load_config(None if config_path is None else Path(config_path))
    KittenRuntime(resolved).run_program(instance, connect_timeout=connect_timeout)
    if isinstance(instance, _FunctionProgram) and instance.interrupted:
        raise MissionInterruptedError(
            "mission stopped because the KSA connection was lost; "
            "check the game, run `kittenio doctor`, then rerun the mission explicitly"
        )
