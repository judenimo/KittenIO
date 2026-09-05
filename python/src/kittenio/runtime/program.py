"""Public flight-program base class and runner."""

from __future__ import annotations

from collections.abc import Iterable
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


def run(
    program: type[ProgramT] | ProgramT,
    *,
    config: Config | None = None,
    config_path: str | Path | None = None,
) -> None:
    """Run an explicit program class or instance until it stops."""

    from .engine import KittenRuntime

    instance = program() if isinstance(program, type) else program
    if not isinstance(instance, KittenProgram):
        raise TypeError("program must be a KittenProgram class or instance")
    resolved = config or load_config(None if config_path is None else Path(config_path))
    KittenRuntime(resolved).run_program(instance)
