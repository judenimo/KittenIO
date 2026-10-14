"""Session-bound public vehicle handles and the bounded discovery registry."""

from __future__ import annotations

import threading
from collections import OrderedDict
from collections.abc import Iterator
from contextlib import contextmanager
from typing import TYPE_CHECKING
from weakref import WeakValueDictionary

from ..research.transforms import rotate_vector
from ..wire.common import (
    CommandResult,
    Vector3,
    VehicleInfo,
)
from ..wire.models import AppliedControlTelemetry, ThrustVectorControl, VehicleState
from .control import ControlError

if TYPE_CHECKING:
    from ..runtime.engine import KittenRuntime


_MAX_VEHICLE_HANDLES = 64


class VehicleActivationError(RuntimeError):
    """An active-vehicle transition failed with a stable application code."""

    def __init__(self, code: str, message: str) -> None:
        self.code = code
        self.message = message
        super().__init__(f"{code}: {message}")


class VehicleRegistry:
    """Read-only, bounded view of vehicles visible in one protocol session."""

    __slots__ = (
        "_cache",
        "_context",
        "_handles",
        "_lock",
        "_order",
        "_runtime",
        "_valid",
    )

    def __init__(self, runtime: "KittenRuntime", context: object | None) -> None:
        self._runtime = runtime
        self._context = context
        self._lock = threading.RLock()
        self._handles: OrderedDict[str, Vehicle] = OrderedDict()
        self._cache: WeakValueDictionary[str, Vehicle] = WeakValueDictionary()
        self._order: tuple[str, ...] = ()
        self._valid = context is not None

    def __getitem__(self, vehicle_id: str) -> "Vehicle":
        if not isinstance(vehicle_id, str):
            raise TypeError("vehicle IDs are opaque strings; integer indexing is unsupported")
        with self._lock:
            if not self._valid or vehicle_id not in self._order:
                raise KeyError(vehicle_id)
            return self._handles[vehicle_id]

    def get(self, vehicle_id: str) -> "Vehicle | None":
        if not isinstance(vehicle_id, str):
            raise TypeError("vehicle IDs are opaque strings; integer indexing is unsupported")
        with self._lock:
            if not self._valid or vehicle_id not in self._order:
                return None
            return self._handles[vehicle_id]

    def named(self, name: str) -> tuple["Vehicle", ...]:
        if not isinstance(name, str):
            raise TypeError("vehicle name must be a string")
        folded = name.casefold()
        with self._lock:
            if not self._valid:
                return ()
            return tuple(
                self._handles[vehicle_id]
                for vehicle_id in self._order
                if self._handles[vehicle_id].name.casefold() == folded
            )

    def __iter__(self) -> Iterator["Vehicle"]:
        with self._lock:
            snapshot = tuple(self._handles[vehicle_id] for vehicle_id in self._order) if self._valid else ()
        return iter(snapshot)

    def __len__(self) -> int:
        with self._lock:
            return len(self._order) if self._valid else 0

    def _replace(self, entries: tuple[VehicleInfo, ...]) -> None:
        """Replace availability while retaining handle identity and metadata."""

        if len(entries) > _MAX_VEHICLE_HANDLES:  # pragma: no cover - runtime caps first
            raise ValueError("vehicle registry capacity exceeded")
        with self._lock:
            retained_ids = {info.id for info in entries}
            for vehicle_id, handle in tuple(self._handles.items()):
                if vehicle_id not in retained_ids:
                    self._retire_locked(vehicle_id, handle)

            order: list[str] = []
            handles: OrderedDict[str, Vehicle] = OrderedDict()
            for info in entries:
                handle = self._cache.get(info.id)
                if handle is None or not handle._valid:
                    handle = Vehicle(
                        self._runtime,
                        self._context,
                        info.id,
                        name=info.name,
                        kind=info.kind,
                    )
                    self._cache[info.id] = handle
                else:
                    handle._update_metadata(info.name, info.kind)
                handles[info.id] = handle
                order.append(info.id)
            self._handles = handles
            self._order = tuple(order)
            self._valid = self._context is not None

    def _retire_locked(self, vehicle_id: str, handle: "Vehicle") -> None:
        """Permanently retire one ID incarnation while holding ``_lock``."""

        handle._invalidate()
        if self._cache.get(vehicle_id) is handle:
            self._cache.pop(vehicle_id, None)

    def _ensure_active(self, state: VehicleState) -> "Vehicle":
        """Admit telemetry before catalog publication without fabricating state."""

        with self._lock:
            handle = self._cache.get(state.id)
            if handle is None or not handle._valid:
                handle = Vehicle(
                    self._runtime,
                    self._context,
                    state.id,
                    name=state.name,
                    kind="vehicle",
                )
                self._cache[state.id] = handle
            else:
                handle._update_metadata(state.name, handle.kind or "vehicle")
            order = [state.id, *(item for item in self._order if item != state.id)]
            self._order = tuple(order[:_MAX_VEHICLE_HANDLES])
            for vehicle_id, previous in tuple(self._handles.items()):
                if vehicle_id not in self._order:
                    self._retire_locked(vehicle_id, previous)
            self._handles = OrderedDict(
                (vehicle_id, handle if vehicle_id == state.id else self._handles[vehicle_id])
                for vehicle_id in self._order
            )
            self._valid = self._context is not None
            return handle

    def _invalidate(self) -> None:
        with self._lock:
            handles = {
                *self._handles.values(),
                *tuple(self._cache.values()),
            }
            for handle in handles:
                handle._invalidate()
            self._valid = False
            self._order = ()
            self._handles.clear()
            self._cache.clear()

    def _infos(self) -> tuple[VehicleInfo, ...]:
        with self._lock:
            if not self._valid:
                return ()
            return tuple(
                VehicleInfo(handle.id, handle.name, handle.kind)
                for handle in (self._handles[vehicle_id] for vehicle_id in self._order)
            )


class Vehicle:
    """An identity-stable handle bound to one exact protocol-session vehicle."""

    __slots__ = (
        "__weakref__",
        "_context",
        "_control",
        "_id",
        "_kind",
        "_name",
        "_runtime",
        "_valid",
    )

    def __init__(
        self,
        runtime: "KittenRuntime",
        context: object | None,
        vehicle_id: str,
        *,
        name: str,
        kind: str,
    ) -> None:
        self._runtime = runtime
        self._context = context
        self._id = vehicle_id
        self._name = name
        self._kind = kind
        self._valid = context is not None
        self._control = VehicleControl(self)

    @property
    def id(self) -> str:
        return self._id

    @property
    def name(self) -> str:
        return self._name

    @property
    def kind(self) -> str:
        return self._kind

    @property
    def is_available(self) -> bool:
        return self._valid and self._runtime._vehicle_is_available(self._context, self._id, self)

    @property
    def is_active(self) -> bool:
        return self._valid and self._runtime._vehicle_is_active(self._context, self._id, self)

    @property
    def state(self) -> VehicleState | None:
        if not self._valid:
            return None
        return self._runtime._vehicle_state(self._context, self._id, self)

    @property
    def control(self) -> "VehicleControl":
        return self._control

    def make_active(self, timeout: float | None = None) -> None:
        """Make this exact session-bound handle KSA's active vehicle."""

        self._runtime._vehicle_make_active(self, timeout=timeout)

    def _update_metadata(self, name: str, kind: str) -> None:
        if not self._valid:
            return
        self._name = name
        self._kind = kind

    def _invalidate(self) -> None:
        self._valid = False

    def _require_valid(self) -> None:
        if not self._valid:
            raise ControlError("the vehicle handle is unavailable in this protocol session")


class VehicleControl:
    """Exact-bound control facade for a :class:`Vehicle` handle."""

    __slots__ = ("_vehicle",)

    def __init__(self, vehicle: Vehicle) -> None:
        self._vehicle = vehicle

    @property
    def telemetry(self) -> AppliedControlTelemetry | None:
        if not self._vehicle._valid:
            return None
        return self._vehicle._runtime._vehicle_control_telemetry(
            self._vehicle._context, self._vehicle.id, self._vehicle
        )

    @property
    def lease_active(self) -> bool:
        if not self._vehicle._valid:
            return False
        return self._vehicle._runtime._vehicle_lease_active(self._vehicle._context, self._vehicle.id, self._vehicle)

    @property
    def lease_id(self) -> str | None:
        if not self._vehicle._valid:
            return None
        return self._vehicle._runtime._vehicle_lease_id(self._vehicle._context, self._vehicle.id, self._vehicle)

    @property
    def override(self) -> ThrustVectorControl | None:
        if not self._vehicle._valid:
            return None
        return self._vehicle._runtime._vehicle_override(self._vehicle._context, self._vehicle.id, self._vehicle)

    def acquire(self, *, timeout: float | None = None) -> str:
        self._vehicle._require_valid()
        return self._vehicle._runtime._vehicle_acquire(
            self._vehicle._context,
            self._vehicle.id,
            self._vehicle,
            timeout=timeout,
        )

    @contextmanager
    def leased(self, *, timeout: float | None = None) -> Iterator["VehicleControl"]:
        """Acquire this vehicle and always release its lease on scope exit."""

        self.acquire(timeout=timeout)
        try:
            yield self
        finally:
            self.release(timeout=timeout)

    def release(self, *, timeout: float | None = None) -> None:
        if not self._vehicle._valid:
            return
        self._vehicle._runtime._vehicle_release(
            self._vehicle._context,
            self._vehicle.id,
            self._vehicle,
            timeout=timeout,
        )

    def set_engine(self, engine_on: bool | None) -> None:
        self._vehicle._require_valid()
        self._vehicle._runtime._vehicle_control_call(
            self._vehicle._context,
            self._vehicle.id,
            self._vehicle,
            "set_engine",
            engine_on,
        )

    def set_throttle(self, throttle: float | None) -> None:
        self._vehicle._require_valid()
        self._vehicle._runtime._vehicle_control_call(
            self._vehicle._context,
            self._vehicle.id,
            self._vehicle,
            "set_throttle",
            throttle,
        )

    def set_thrust_vector(
        self,
        direction_cci: Vector3 | tuple[float, float, float],
        *,
        engine_on: bool | None = None,
        throttle: float | None = None,
    ) -> None:
        """Send the desired vehicle +X thrust direction in CCI coordinates."""

        self._vehicle._require_valid()
        self._vehicle._runtime._vehicle_control_call(
            self._vehicle._context,
            self._vehicle.id,
            self._vehicle,
            "set_thrust_vector",
            direction_cci,
            engine_on=engine_on,
            throttle=throttle,
        )

    def hold_current_direction(
        self,
        *,
        engine_on: bool | None = None,
        throttle: float | None = None,
    ) -> None:
        """Hold the current vehicle +X thrust direction in inertial CCI space.

        Requires an explicit lease and orientation telemetry. This initializes
        the complete control frame, so later ``set_throttle`` calls can amend
        it. Roll is not controlled. Engine/throttle defaults leave those inputs
        under player control.
        """
        self._vehicle._require_valid()
        state = self._vehicle.state
        if state is None or state.body_to_cci is None:
            raise ControlError("current vehicle orientation is unavailable; wait for orientation telemetry")
        direction = rotate_vector(Vector3(1.0, 0.0, 0.0), state.body_to_cci)
        self.set_thrust_vector(direction, engine_on=engine_on, throttle=throttle)

    def clear_overrides(self) -> None:
        self._vehicle._require_valid()
        self._vehicle._runtime._vehicle_control_call(
            self._vehicle._context,
            self._vehicle.id,
            self._vehicle,
            "clear_overrides",
        )

    def stage(self, *, command_id: str | None = None, timeout: float | None = None) -> CommandResult:
        self._vehicle._require_valid()
        return self._vehicle._runtime._vehicle_control_call(
            self._vehicle._context,
            self._vehicle.id,
            self._vehicle,
            "stage",
            command_id=command_id,
            timeout=timeout,
        )

    def set_target(
        self,
        target_id: str,
        *,
        command_id: str | None = None,
        timeout: float | None = None,
    ) -> CommandResult:
        self._vehicle._require_valid()
        return self._vehicle._runtime._vehicle_control_call(
            self._vehicle._context,
            self._vehicle.id,
            self._vehicle,
            "set_target",
            target_id,
            command_id=command_id,
            timeout=timeout,
        )
