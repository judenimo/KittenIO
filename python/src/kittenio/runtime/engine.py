"""Network/user-thread separation and mission execution runtime."""

from __future__ import annotations

import asyncio
import math
import queue
import threading
import time
from collections import OrderedDict
from collections.abc import Mapping
from concurrent.futures import Future
from concurrent.futures import TimeoutError as FutureTimeoutError
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any, Coroutine
from uuid import uuid4

from ..api.control import ControlClient, ControlError
from ..api.time_warp import TimeWarp
from ..api.vehicle import Vehicle, VehicleActivationError, VehicleRegistry
from ..transport.client import BridgeClient, NotConnectedError
from ..transport.config import Config
from ..wire.common import (
    Catalog,
    CommandError,
    CommandResult,
    ConnectionInfo,
    ManualControlState,
    VehicleInfo,
)
from ..wire.models import (
    AppliedControlTelemetry,
    CapabilitySet,
    FlightClock,
    FlightState,
    LandingSiteDefinition,
    ThrustVectorControl,
    UnsupportedCapabilityError,
    VehicleState,
)
from ..wire.protocol import Envelope, ProtocolError

if TYPE_CHECKING:
    from .program import KittenProgram
    from .waits import WaitCondition


_MAX_SESSIONS = 8
_MAX_VEHICLE_HANDLES = 64
_MAX_PENDING_REQUESTS = 64
_MAX_CROSS_THREAD_SUBMISSIONS = 64
_MAX_LIFECYCLE_EVENTS = 64
_CONTROL_RENEWAL_GRACE_S = 2.0
_MAX_CONTROL_RENEWAL_INTERVAL_S = 0.2

_EMPTY_CATALOG = Catalog()
_SAFE_CAPABILITIES = CapabilitySet.unavailable()
_DISCONNECTED = ConnectionInfo(False)


class RuntimeErrorBase(RuntimeError):
    """Base class for SDK runtime errors."""


class RequestTimeoutError(RuntimeErrorBase):
    pass


class RemoteError(RuntimeErrorBase):
    pass


class RuntimeOverloadError(RuntimeErrorBase):
    """Raised when bounded runtime work cannot be admitted safely."""


class LifecycleOverflowError(RuntimeOverloadError):
    """A full lifecycle queue is a terminal runtime fault."""


@dataclass(frozen=True, slots=True)
class _ControllerKey:
    controller_id: str


@dataclass(frozen=True, slots=True)
class _SessionKey:
    controller: _ControllerKey
    game_instance_id: str
    session_id: str
    protocol_version: int = 1


@dataclass(frozen=True, slots=True)
class _VehicleKey:
    session: _SessionKey
    vehicle_id: str


@dataclass(frozen=True, slots=True)
class _PendingRequestKey:
    controller: _ControllerKey
    game_instance_id: str
    session_id: str
    request_id: str

    @classmethod
    def for_session(cls, session: _SessionKey, request_id: str) -> "_PendingRequestKey":
        return cls(
            controller=session.controller,
            game_instance_id=session.game_instance_id,
            session_id=session.session_id,
            request_id=request_id,
        )

    def belongs_to(self, session: _SessionKey) -> bool:
        return (
            self.controller == session.controller
            and self.game_instance_id == session.game_instance_id
            and self.session_id == session.session_id
        )


@dataclass(slots=True)
class _SessionState:
    key: _SessionKey
    connection: ConnectionInfo
    capabilities: CapabilitySet
    registry: VehicleRegistry
    state: FlightState | None = None
    source_catalog: Catalog = _EMPTY_CATALOG
    catalog: Catalog = _EMPTY_CATALOG
    registry_projection: object | None = None
    state_received_monotonic_s: float | None = None
    transport_rtt_s: float | None = None

    def reset(self) -> None:
        self.connection = _DISCONNECTED
        self.capabilities = _SAFE_CAPABILITIES
        self.state = None
        self.source_catalog = _EMPTY_CATALOG
        self.catalog = _EMPTY_CATALOG
        self.registry_projection = None
        self.state_received_monotonic_s = None
        self.transport_rtt_s = None
        self.registry._invalidate()


@dataclass(slots=True)
class _PendingRequest:
    key: _PendingRequestKey
    accepted_types: frozenset[str]
    event: threading.Event
    envelope: Envelope | None = None
    error: BaseException | None = None


@dataclass(frozen=True, slots=True)
class _LifecycleEvent:
    name: str
    session: _SessionKey | None
    value: Any


def _latest_put(target: queue.Queue[Any], value: Any) -> None:
    try:
        target.put_nowait(value)
        return
    except queue.Full:
        pass
    try:
        target.get_nowait()
    except queue.Empty:
        pass
    target.put_nowait(value)


def _dotnet_ordinal_key(value: str) -> bytes:
    """Match ``StringComparer.Ordinal`` by comparing UTF-16 code units."""

    return value.encode("utf-16-be", errors="surrogatepass")


class KsaApi:
    """State and control surface exposed as ``program.ksa``."""

    def __init__(self, runtime: "KittenRuntime") -> None:
        self._runtime = runtime
        self.time_warp = TimeWarp(runtime)

    @property
    def data(self) -> FlightState | None:
        return self._runtime.data

    @property
    def clock(self) -> FlightClock | None:
        return self._runtime.clock

    @property
    def state_age_s(self) -> float | None:
        """Real monotonic age of the most recently received telemetry frame."""

        return self._runtime.state_age_s

    @property
    def transport_rtt_s(self) -> float | None:
        """Latest session heartbeat round-trip time, in real seconds."""

        return self._runtime.transport_rtt_s

    @property
    def active_vehicle(self) -> Vehicle | None:
        return self._runtime.active_vehicle

    @property
    def vehicles(self) -> VehicleRegistry:
        return self._runtime.vehicle_registry

    @property
    def capabilities(self) -> CapabilitySet:
        return self._runtime.capabilities

    @property
    def catalog(self) -> Catalog:
        return self._runtime.catalog

    @property
    def manual_control(self) -> ManualControlState | None:
        data = self._runtime.data
        return None if data is None else getattr(data, "manual_control", None)

    @property
    def connection(self) -> ConnectionInfo:
        return self._runtime.connection_info

    def set_landing_site(
        self,
        site: LandingSiteDefinition,
        *,
        timeout: float | None = None,
    ) -> CommandResult:
        """Set the session-local, body-fixed CCF landing reference."""

        if not isinstance(site, LandingSiteDefinition):
            raise TypeError("site must be LandingSiteDefinition")
        if not self.capabilities.landing.site_definition:
            raise UnsupportedCapabilityError("landing references are unavailable")
        envelope = self._runtime.request(
            "request",
            {"method": "set_landing_site", "command_id": str(uuid4()), "params": site.to_payload()},
            timeout=timeout,
            accepted_types={"response"},
        )
        ok = bool(envelope.payload["ok"])
        if ok:
            return CommandResult(ok=True, result=envelope.payload["result"])
        error = envelope.payload["error"]
        return CommandResult(ok=False, error=CommandError(error["code"], error["message"]))


class KittenRuntime:
    """Owns the asyncio network thread and runs user code on the caller thread."""

    def __init__(self, config: Config) -> None:
        self.config = config
        self._controller_key = _ControllerKey(str(uuid4()))
        self._lock = threading.RLock()
        self._vehicle_changed = threading.Condition(self._lock)
        self._sessions: OrderedDict[_SessionKey, _SessionState] = OrderedDict()
        self._active_session: _SessionKey | None = None
        self._control = ControlClient(self)
        self._empty_registry = VehicleRegistry(self, None)
        self.ksa = KsaApi(self)
        self._states: queue.Queue[tuple[_SessionKey, FlightState]] = queue.Queue(maxsize=1)
        self._lifecycle: queue.Queue[_LifecycleEvent] = queue.Queue(maxsize=_MAX_LIFECYCLE_EVENTS)
        self._pending: dict[_PendingRequestKey, _PendingRequest] = {}
        self._pending_lock = threading.Lock()
        self._submission_slots = threading.BoundedSemaphore(_MAX_CROSS_THREAD_SUBMISSIONS)
        self._loop: asyncio.AbstractEventLoop | None = None
        self._client: BridgeClient | None = None
        self._thread: threading.Thread | None = None
        self._control_renewal_thread: threading.Thread | None = None
        self._mission_progress_monotonic_s: float | None = None
        self._ready = threading.Event()
        self._network_stopped = threading.Event()
        self._network_stopped.set()
        self._connection_ready = threading.Event()
        self._startup_error: BaseException | None = None
        self._stopping = threading.Event()
        self._terminal_lock = threading.Lock()
        self._terminal_error: BaseException | None = None
        self._terminal_stop_scheduled = False

    def _active_state_locked(self) -> _SessionState | None:
        if self._active_session is None:
            return None
        return self._sessions.get(self._active_session)

    @property
    def data(self) -> FlightState | None:
        with self._lock:
            active = self._active_state_locked()
            return None if active is None else active.state

    @property
    def clock(self) -> FlightClock | None:
        data = self.data
        return None if data is None else FlightClock.from_state(data)

    @property
    def state_age_s(self) -> float | None:
        with self._lock:
            active = self._active_state_locked()
            received = None if active is None else active.state_received_monotonic_s
        return None if received is None else max(0.0, time.monotonic() - received)

    @property
    def transport_rtt_s(self) -> float | None:
        with self._lock:
            active = self._active_state_locked()
            return None if active is None else active.transport_rtt_s

    @property
    def vehicle_registry(self) -> VehicleRegistry:
        with self._lock:
            active = self._active_state_locked()
            return self._empty_registry if active is None else active.registry

    @property
    def active_vehicle(self) -> Vehicle | None:
        with self._lock:
            active = self._active_state_locked()
            if active is None or active.state is None or active.state.vehicle is None:
                return None
            return active.registry.get(active.state.vehicle.id)

    @property
    def catalog(self) -> Catalog:
        with self._lock:
            active = self._active_state_locked()
            return _EMPTY_CATALOG if active is None else active.catalog

    @property
    def capabilities(self) -> CapabilitySet:
        with self._lock:
            active = self._active_state_locked()
            return _SAFE_CAPABILITIES if active is None else active.capabilities

    @property
    def connection_info(self) -> ConnectionInfo:
        with self._lock:
            active = self._active_state_locked()
            return _DISCONNECTED if active is None else active.connection

    def _sync_vehicle_registry_locked(self, session: _SessionState) -> None:
        active_state = None if session.state is None else session.state.vehicle
        projection = (
            None if active_state is None else (active_state.id, active_state.name),
            session.source_catalog,
        )
        if projection == session.registry_projection:
            return
        by_id = {item.id: item for item in session.source_catalog.vehicles}
        selected: list[VehicleInfo] = []
        if active_state is not None:
            selected.append(
                by_id.pop(
                    active_state.id,
                    VehicleInfo(active_state.id, active_state.name, "vehicle"),
                )
            )
        remaining = sorted(by_id.values(), key=lambda item: _dotnet_ordinal_key(item.id))
        selected.extend(remaining[: _MAX_VEHICLE_HANDLES - len(selected)])
        admitted = tuple(selected)
        session.registry._replace(admitted)
        session.catalog = Catalog(
            bodies=session.source_catalog.bodies,
            vehicles=session.registry._infos(),
        )
        session.registry_projection = projection

    def _vehicle_is_available(self, context: object, vehicle_id: str, handle: Vehicle | None = None) -> bool:
        with self._lock:
            active = self._active_state_locked()
            current = None if active is None else active.registry.get(vehicle_id)
            return (
                active is not None
                and active.key == context
                and active.connection.connected
                and current is not None
                and (handle is None or current is handle)
            )

    def _vehicle_is_active(self, context: object, vehicle_id: str, handle: Vehicle | None = None) -> bool:
        with self._lock:
            active = self._active_state_locked()
            state = None if active is None else active.state
            current = None if active is None else active.registry.get(vehicle_id)
            return (
                active is not None
                and active.key == context
                and active.connection.connected
                and state is not None
                and state.vehicle is not None
                and state.vehicle.id == vehicle_id
                and current is not None
                and (handle is None or current is handle)
            )

    def _vehicle_state(
        self,
        context: object,
        vehicle_id: str,
        handle: Vehicle | None = None,
    ) -> VehicleState | None:
        with self._lock:
            active = self._active_state_locked()
            state = None if active is None else active.state
            current = None if active is None else active.registry.get(vehicle_id)
            if (
                active is None
                or active.key != context
                or not active.connection.connected
                or state is None
                or state.vehicle is None
                or state.vehicle.id != vehicle_id
                or current is None
                or (handle is not None and current is not handle)
            ):
                return None
            return state.vehicle

    def _vehicle_control_telemetry(
        self,
        context: object,
        vehicle_id: str,
        handle: Vehicle | None = None,
    ) -> AppliedControlTelemetry | None:
        with self._lock:
            active = self._active_state_locked()
            state = None if active is None else active.state
            current = None if active is None else active.registry.get(vehicle_id)
            if (
                active is None
                or active.key != context
                or not active.connection.connected
                or state is None
                or state.vehicle is None
                or state.vehicle.id != vehicle_id
                or current is None
                or (handle is not None and current is not handle)
            ):
                return None
            return state.applied_control

    def _require_vehicle_control_context(
        self,
        context: object,
        vehicle_id: str,
        handle: Vehicle | None = None,
    ) -> _SessionKey:
        with self._lock:
            active = self._active_state_locked()
            state = None if active is None else active.state
            current = None if active is None else active.registry.get(vehicle_id)
            if (
                not isinstance(context, _SessionKey)
                or active is None
                or active.key != context
                or not active.connection.connected
                or current is None
                or (handle is not None and current is not handle)
            ):
                raise ControlError("the vehicle handle is unavailable in this protocol session")
            if state is None or state.vehicle is None or state.vehicle.id != vehicle_id:
                raise ControlError("only KSA's active vehicle can be controlled")
            if not state.vehicle.controllable:
                raise ControlError("KSA's active vehicle is not controllable")
            if not active.capabilities.control.thrust_vector_target:
                raise ControlError("external thrust-vector control is unavailable")
            return context

    def _vehicle_control_context_is_current(
        self,
        context: object,
        vehicle_id: str,
        handle: Vehicle | None = None,
    ) -> bool:
        with self._lock:
            active = self._active_state_locked()
            state = None if active is None else active.state
            current = None if active is None else active.registry.get(vehicle_id)
            return bool(
                isinstance(context, _SessionKey)
                and active is not None
                and active.key == context
                and active.connection.connected
                and current is not None
                and (handle is None or current is handle)
                and state is not None
                and state.vehicle is not None
                and state.vehicle.id == vehicle_id
                and state.vehicle.controllable
                and active.capabilities.control.thrust_vector_target
            )

    def _vehicle_lease_active(self, context: object, vehicle_id: str, handle: Vehicle | None = None) -> bool:
        return self._control._lease_active_for(context, vehicle_id, handle)

    def _vehicle_lease_id(self, context: object, vehicle_id: str, handle: Vehicle | None = None) -> str | None:
        return self._control._lease_id_for(context, vehicle_id, handle)

    def _vehicle_override(
        self,
        context: object,
        vehicle_id: str,
        handle: Vehicle | None = None,
    ) -> ThrustVectorControl | None:
        return self._control._override_for(context, vehicle_id, handle)

    def _vehicle_acquire(
        self,
        context: object,
        vehicle_id: str,
        handle: Vehicle,
        *,
        timeout: float | None,
    ) -> str:
        session = self._require_vehicle_control_context(context, vehicle_id, handle)
        return self._control._acquire_for(session, vehicle_id, handle, timeout=timeout)

    def _vehicle_release(
        self,
        context: object,
        vehicle_id: str,
        handle: Vehicle,
        *,
        timeout: float | None,
    ) -> None:
        self._control._release_for(context, vehicle_id, handle, timeout=timeout)

    def _vehicle_control_call(
        self,
        context: object,
        vehicle_id: str,
        handle: Vehicle,
        method: str,
        *args: object,
        **kwargs: object,
    ) -> Any:
        session = self._require_vehicle_control_context(context, vehicle_id, handle)
        return self._control._call_for(session, vehicle_id, handle, method, *args, **kwargs)

    def _vehicle_make_active(self, vehicle: Vehicle, *, timeout: float | None) -> None:
        """Activate one exact handle and await its authoritative telemetry frame."""

        duration = timeout if timeout is not None else self.config.request_timeout_s
        deadline = self._deadline(duration, "vehicle activation")
        context = vehicle._context
        vehicle_id = vehicle.id

        with self._vehicle_changed:
            active = self._active_state_locked()
            if (
                not isinstance(context, _SessionKey)
                or active is None
                or active.key != context
                or not active.connection.connected
            ):
                raise NotConnectedError("the vehicle handle's KSA protocol session is no longer connected")
            if active.registry.get(vehicle_id) is not vehicle:
                raise VehicleActivationError(
                    "vehicle_not_found",
                    "the vehicle handle is no longer available in this protocol session",
                )
            before_frame = -1 if active.state is None else active.state.frame_number

        response = self._request_in_session(
            context,
            "request",
            {
                "method": "set_active_vehicle",
                "command_id": str(uuid4()),
                "params": {"vehicle_id": vehicle_id},
            },
            accepted_types={"response"},
            _deadline_monotonic=deadline,
        )
        payload = response.payload
        if payload.get("ok") is not True:
            error = payload.get("error")
            # Normal network traffic has already passed the stricter peer
            # validator. Keep these guards for direct/custom transports.
            if not isinstance(error, Mapping):
                raise ProtocolError("vehicle activation response.error must be an object")
            code = error.get("code")
            message = error.get("message")
            if not isinstance(code, str) or not code:
                raise ProtocolError("vehicle activation response.error.code must be a non-empty string")
            if not isinstance(message, str) or not message:
                raise ProtocolError("vehicle activation response.error.message must be a non-empty string")
            raise VehicleActivationError(code, message)

        result = payload.get("result")
        if not isinstance(result, Mapping) or set(result) != {"vehicle_id"} or result.get("vehicle_id") != vehicle_id:
            if not isinstance(result, Mapping):
                raise ProtocolError("vehicle activation response.result must be an object")
            if set(result) != {"vehicle_id"} or result.get("vehicle_id") != vehicle_id:
                raise ProtocolError("vehicle activation response did not identify the requested vehicle")

        with self._vehicle_changed:
            while True:
                active = self._active_state_locked()
                if active is None or active.key != context or not active.connection.connected:
                    raise NotConnectedError("the KSA protocol session changed during vehicle activation")
                current = active.registry.get(vehicle_id)
                if current is not vehicle:
                    raise VehicleActivationError(
                        "switch_failed",
                        "the requested vehicle handle changed before activation was confirmed",
                    )
                state = active.state
                if (
                    state is not None
                    and state.frame_number > before_frame
                    and state.vehicle is not None
                    and state.vehicle.id == vehicle_id
                ):
                    return
                remaining = deadline - time.monotonic()
                if remaining <= 0.0:
                    raise RequestTimeoutError("timed out waiting for authoritative active-vehicle telemetry")
                self._vehicle_changed.wait(remaining)

    def _require_active_session(self) -> tuple[_SessionKey, ConnectionInfo]:
        with self._lock:
            active = self._active_state_locked()
            if active is not None and active.connection.connected:
                return active.key, active.connection
        # The welcome frame is buffered immediately before the async connect
        # callback updates the runtime store. BridgeClient's peer state tells us
        # whether this is that narrow hand-off rather than a disconnected wait.
        client = self._client
        if client is not None and client.connected:
            self._connection_ready.wait(min(self.config.request_timeout_s, 0.25))
            with self._lock:
                active = self._active_state_locked()
                if active is not None and active.connection.connected:
                    return active.key, active.connection
        raise NotConnectedError("the KSA mod is not connected")

    def start(self, *, timeout: float = 5.0) -> None:
        if self._thread is not None and self._thread.is_alive():
            if self._stopping.is_set():
                raise RuntimeErrorBase("the KittenIO client is still stopping")
            return
        self._ready.clear()
        self._network_stopped.clear()
        self._stopping.clear()
        self._startup_error = None
        self._mission_progress_monotonic_s = None
        with self._terminal_lock:
            self._terminal_error = None
            self._terminal_stop_scheduled = False
        self._thread = threading.Thread(target=self._network_thread, name="kittenio-network", daemon=True)
        self._thread.start()
        if not self._ready.wait(timeout):
            # Startup may be suspended inside the socket implementation.  This
            # stop request must outlive the caller's timeout: _network_main also
            # observes it after start() returns and closes any late connection.
            self._stopping.set()
            self._schedule_client_stop()
            raise RuntimeErrorBase("timed out starting the KittenIO client")
        if self._startup_error is not None:
            raise RuntimeErrorBase(f"could not start KittenIO client: {self._startup_error}") from self._startup_error
        self._control_renewal_thread = threading.Thread(
            target=self._control_renewal_worker,
            name="kittenio-control-renewal",
            daemon=True,
        )
        self._control_renewal_thread.start()

    def _schedule_client_stop(self) -> Future[None] | None:
        """Submit a durable stop request without cancelling it on caller timeout."""

        loop, client = self._loop, self._client
        if loop is None or client is None or not loop.is_running():
            return None
        stop_coroutine = client.stop()
        try:
            return asyncio.run_coroutine_threadsafe(stop_coroutine, loop)
        except RuntimeError:
            stop_coroutine.close()
            return None

    def stop(self, *, timeout: float = 5.0) -> None:
        self._stopping.set()
        wait_seconds = max(0.0, float(timeout))
        deadline = time.monotonic() + wait_seconds
        future = self._schedule_client_stop()
        if future is not None:
            try:
                future.result(timeout=max(0.0, deadline - time.monotonic()))
            except Exception:
                # In particular, a caller timeout must not cancel the only
                # queued shutdown request.  The network thread owns completion.
                pass
        if self._thread is not None and self._thread is not threading.current_thread():
            self._thread.join(max(0.0, deadline - time.monotonic()))
        if self._control_renewal_thread is not None and self._control_renewal_thread is not threading.current_thread():
            self._control_renewal_thread.join(max(0.0, deadline - time.monotonic()))
        self._fail_pending(NotConnectedError("KittenIO runtime stopped"))
        self._control._invalidate()
        with self._lock:
            for session in self._sessions.values():
                session.reset()
            self._active_session = None
            self._vehicle_changed.notify_all()
        self._connection_ready.clear()
        self._mission_progress_monotonic_s = None
        self._drain_states()
        self._drain_lifecycle_queue()

    def _mark_mission_progress(self) -> None:
        with self._lock:
            self._mission_progress_monotonic_s = time.monotonic()

    def _control_renewal_worker(self) -> None:
        """Renew the last command through short UI/telemetry transitions.

        Renewal remains bounded by recent mission-thread progress. A wedged
        callback therefore holds its last command for at most the grace window
        plus the server dead-man timeout, while a brief map-view transition can
        pass without dropping the exact-vehicle lease.
        """

        interval_s = min(
            _MAX_CONTROL_RENEWAL_INTERVAL_S,
            max(0.05, self.config.command_deadman_ms / 3_000.0),
        )
        while not self._stopping.wait(interval_s):
            with self._lock:
                last_progress_s = self._mission_progress_monotonic_s
            if last_progress_s is None or time.monotonic() - last_progress_s > _CONTROL_RENEWAL_GRACE_S:
                continue
            try:
                self._control._refresh()
            except BaseException as exc:
                if not self._stopping.is_set():
                    self._signal_terminal_fault(exc)
                return

    @staticmethod
    def _deadline(duration: float, operation: str) -> float:
        if isinstance(duration, bool) or not isinstance(duration, (int, float)):
            raise TypeError(f"{operation} timeout must be numeric")
        value = float(duration)
        if not math.isfinite(value) or value <= 0.0:
            raise ValueError(f"{operation} timeout must be finite and positive")
        return time.monotonic() + value

    def _run_submission(
        self,
        coroutine: Coroutine[Any, Any, None],
        *,
        deadline: float,
        operation: str,
    ) -> None:
        if not self._submission_slots.acquire(blocking=False):
            coroutine.close()
            raise RuntimeOverloadError(
                f"KittenIO cross-thread submission capacity ({_MAX_CROSS_THREAD_SUBMISSIONS}) is full"
            )
        loop = self._loop
        if loop is None or not loop.is_running():
            coroutine.close()
            self._submission_slots.release()
            raise NotConnectedError("KittenIO client is not running")

        completion: Future[None] = Future()
        state_lock = threading.Lock()
        task: asyncio.Task[None] | None = None
        cancel_requested = False
        finished = False

        def finish(*, error: BaseException | None = None, cancelled: bool = False) -> None:
            nonlocal finished
            with state_lock:
                if finished:
                    return
                finished = True
            try:
                if cancelled:
                    completion.cancel()
                elif error is not None:
                    completion.set_exception(error)
                else:
                    completion.set_result(None)
            finally:
                self._submission_slots.release()

        def task_done(done: asyncio.Task[None]) -> None:
            if done.cancelled():
                finish(cancelled=True)
                return
            try:
                error = done.exception()
            except BaseException as exc:  # pragma: no cover - defensive Task API guard
                finish(error=exc)
                return
            finish(error=error)

        def begin_on_loop() -> None:
            nonlocal task
            close_without_start = False
            creation_error: BaseException | None = None
            with state_lock:
                if finished:
                    return
                if cancel_requested:
                    close_without_start = True
                else:
                    try:
                        task = loop.create_task(coroutine)
                    except BaseException as exc:  # pragma: no cover - loop failure
                        creation_error = exc
            if close_without_start:
                coroutine.close()
                finish(cancelled=True)
                return
            if creation_error is not None:
                coroutine.close()
                finish(error=creation_error)
                return
            assert task is not None
            task.add_done_callback(task_done)
            with state_lock:
                should_cancel = cancel_requested
            if should_cancel:
                task.cancel()

        def request_cancel() -> None:
            nonlocal cancel_requested
            with state_lock:
                cancel_requested = True

            def cancel_on_loop() -> None:
                with state_lock:
                    current = task
                if current is not None:
                    current.cancel()

            try:
                loop.call_soon_threadsafe(cancel_on_loop)
            except RuntimeError:
                # If shutdown prevented the loop from acknowledging a queued
                # submission, close it here without releasing a live Task's slot.
                with state_lock:
                    never_started = task is None and not finished
                if never_started:
                    coroutine.close()
                    finish(cancelled=True)

        try:
            loop.call_soon_threadsafe(begin_on_loop)
        except BaseException:
            coroutine.close()
            finish(cancelled=True)
            raise

        remaining = deadline - time.monotonic()
        if remaining <= 0.0:
            request_cancel()
            raise RequestTimeoutError(f"timed out while {operation}")
        try:
            completion.result(timeout=remaining)
        except FutureTimeoutError as exc:
            request_cancel()
            raise RequestTimeoutError(f"timed out while {operation}") from exc

    def send(self, message_type: str, payload: dict[str, Any]) -> None:
        session, _ = self._require_active_session()
        self._send_in_session(session, message_type, payload)

    def _send_in_session(
        self,
        session: _SessionKey,
        message_type: str,
        payload: dict[str, Any],
    ) -> None:
        with self._lock:
            active = self._active_state_locked()
            if active is None or active.key != session or not active.connection.connected:
                raise NotConnectedError("the KSA protocol session changed before send")
        client = self._client
        if client is None:
            raise NotConnectedError("KittenIO client is not running")
        deadline = self._deadline(self.config.request_timeout_s, "send")
        sender = getattr(client, "_send_expected", client.send)
        self._run_submission(
            sender(
                message_type,
                payload,
                expected_game_instance_id=session.game_instance_id,
                expected_session_id=session.session_id,
            ),
            deadline=deadline,
            operation=f"sending {message_type}",
        )

    def request(
        self,
        message_type: str,
        payload: dict[str, Any],
        *,
        timeout: float | None = None,
        accepted_types: set[str] | None = None,
    ) -> Envelope:
        session, _ = self._require_active_session()
        return self._request_in_session(
            session,
            message_type,
            payload,
            timeout=timeout,
            accepted_types=accepted_types,
        )

    def _request_in_session(
        self,
        session: _SessionKey,
        message_type: str,
        payload: dict[str, Any],
        *,
        timeout: float | None = None,
        accepted_types: set[str] | None = None,
        _deadline_monotonic: float | None = None,
    ) -> Envelope:
        with self._lock:
            active = self._active_state_locked()
            if active is None or active.key != session or not active.connection.connected:
                raise NotConnectedError("the KSA protocol session changed before request")
        if _deadline_monotonic is None:
            duration = timeout if timeout is not None else self.config.request_timeout_s
            deadline = self._deadline(duration, "request")
        else:
            deadline = _deadline_monotonic
            if deadline <= time.monotonic():
                raise RequestTimeoutError("timed out before request admission")
        request_id = str(uuid4())
        key = _PendingRequestKey.for_session(session, request_id)
        pending = _PendingRequest(
            key=key,
            accepted_types=frozenset(accepted_types or {"response"}),
            event=threading.Event(),
        )
        with self._pending_lock:
            if len(self._pending) >= _MAX_PENDING_REQUESTS:
                raise RuntimeOverloadError(f"KittenIO pending request capacity ({_MAX_PENDING_REQUESTS}) is full")
            self._pending[key] = pending
        try:
            client = self._client
            if client is None:
                raise NotConnectedError("KittenIO client is not running")
            sender = getattr(client, "_send_expected", client.send)
            self._run_submission(
                sender(
                    message_type,
                    payload,
                    request_id=request_id,
                    expected_game_instance_id=session.game_instance_id,
                    expected_session_id=session.session_id,
                ),
                deadline=deadline,
                operation=f"sending {message_type} request",
            )
            remaining = deadline - time.monotonic()
            if remaining <= 0.0 or not pending.event.wait(remaining):
                raise RequestTimeoutError(f"timed out waiting for {message_type} response")
            if pending.error is not None:
                raise pending.error
            if pending.envelope is None:
                raise RuntimeErrorBase("request completed without a response")
            if pending.envelope.type == "error":
                raise RemoteError(str(pending.envelope.payload.get("message", "remote error")))
            return pending.envelope
        finally:
            with self._pending_lock:
                self._pending.pop(key, None)

    def run_program(self, program: "KittenProgram") -> None:
        """Run callbacks on this thread until stopped or interrupted."""

        program._bind(self)
        started_session: _SessionKey | None = None
        previous_vehicle_id: str | None = None
        script: Any = None
        wait: WaitCondition | None = None
        try:
            self.start()
            while not program.stopped:
                self._raise_terminal_fault()
                if self._stopping.is_set():
                    break
                started_session, previous_vehicle_id, script, wait = self._drain_lifecycle(
                    program, started_session, previous_vehicle_id, script, wait
                )
                if program.stopped:
                    break
                try:
                    state_session, state = self._states.get(timeout=0.1)
                except queue.Empty:
                    continue
                # A connection event can arrive while this thread is blocked on
                # first-state delivery. Drain again so on_connect always
                # precedes on_start for that session.
                started_session, previous_vehicle_id, script, wait = self._drain_lifecycle(
                    program, started_session, previous_vehicle_id, script, wait
                )
                if program.stopped:
                    break
                with self._lock:
                    active_session = self._active_session
                    active = self._active_state_locked()
                    connected = active is not None and active.connection.connected
                if not connected or state_session != active_session:
                    continue
                self._mark_mission_progress()
                if state_session != started_session:
                    started_session = state_session
                    program.on_start()
                    self._raise_terminal_fault()
                    if program.stopped:
                        break
                    produced = program.script()
                    self._raise_terminal_fault()
                    script = None if produced is None else iter(produced)
                    wait = None
                    if program.stopped:
                        break
                vehicle_id = state.vehicle.id if state.vehicle else None
                if vehicle_id != previous_vehicle_id:
                    old = previous_vehicle_id
                    previous_vehicle_id = vehicle_id
                    program.on_vehicle_changed(old, vehicle_id)
                    self._raise_terminal_fault()
                    if program.stopped:
                        break
                program.on_tick(state)
                self._raise_terminal_fault()
                if program.stopped:
                    break
                if script is not None:
                    wait = self._step_script(script, wait, state, program)
                    self._raise_terminal_fault()
                    if program.stopped:
                        break
        finally:
            try:
                if self._control.lease_active:
                    self._control.release(timeout=0.5)
            except Exception:
                self._control._invalidate()
            try:
                # A synchronous request made by a user callback can be the
                # operation that discovers malformed authenticated traffic.
                # Its waiter is released only after the network thread queues
                # the exact disconnect, but the callback then unwinds directly
                # into this cleanup path. Deliver queued disconnect callbacks
                # before the terminal on_stop callback.
                self._drain_disconnects_before_stop(program)
            finally:
                try:
                    program.on_stop()
                finally:
                    self.stop()

    def _drain_lifecycle(
        self,
        program: "KittenProgram",
        started_session: _SessionKey | None,
        previous_vehicle_id: str | None,
        script: Any,
        wait: "WaitCondition | None",
    ) -> tuple[_SessionKey | None, str | None, Any, "WaitCondition | None"]:
        while True:
            try:
                event = self._lifecycle.get_nowait()
            except queue.Empty:
                break
            if event.name == "connect":
                program.on_connect(event.value)
            elif event.name == "disconnect":
                if event.session is None or event.session == started_session:
                    started_session = None
                    previous_vehicle_id = None
                    script = None
                    wait = None
                program.on_disconnect(event.value[0], event.value[1])
            elif event.name == "terminal_transport":
                # BridgeClient queues the exact authenticated disconnect
                # before its terminal protocol exception reaches the network
                # thread. Keeping the terminal marker in this same FIFO
                # guarantees connect -> disconnect -> stop callback order.
                raise event.value
            self._raise_terminal_fault()
            if program.stopped:
                break
        return started_session, previous_vehicle_id, script, wait

    def _drain_disconnects_before_stop(self, program: "KittenProgram") -> None:
        while True:
            try:
                event = self._lifecycle.get_nowait()
            except queue.Empty:
                return
            if event.name == "disconnect":
                program.on_disconnect(event.value[0], event.value[1])

    @staticmethod
    def _step_script(
        script: Any,
        wait: "WaitCondition | None",
        state: FlightState,
        program: "KittenProgram",
    ) -> "WaitCondition | None":
        if wait is not None and not wait.ready(state):
            return wait
        try:
            yielded = next(script)
        except StopIteration:
            program.stop()
            return None
        if yielded is None:
            from .waits import next_tick

            return next_tick()
        if not hasattr(yielded, "ready"):
            raise TypeError("flight script must yield a KittenIO wait condition")
        # Arm time- and frame-relative waits at the yield point, not one tick later.
        yielded.ready(state)
        return yielded

    def _network_thread(self) -> None:
        try:
            asyncio.run(self._network_main())
        except BaseException as exc:
            if not self._ready.is_set():
                self._startup_error = exc
                self._ready.set()
            else:
                try:
                    self._queue_lifecycle(_LifecycleEvent("terminal_transport", None, exc))
                except LifecycleOverflowError:
                    # Queue overflow already recorded the higher-priority
                    # terminal runtime fault without blocking the network.
                    pass
        finally:
            self._network_stopped.set()

    async def _network_main(self) -> None:
        self._loop = asyncio.get_running_loop()
        client = BridgeClient(
            self.config,
            on_envelope=self._on_envelope,
            on_connect=self._on_connect,
            on_disconnect=self._on_disconnect,
            on_latency=self._on_latency,
        )
        self._client = client
        try:
            if self._stopping.is_set():
                raise RuntimeErrorBase("KittenIO client startup was cancelled")
            await client.start()
            if self._stopping.is_set():
                await client.stop()
                raise RuntimeErrorBase("KittenIO client startup was cancelled")
            self._ready.set()
            await client.serve_forever()
        finally:
            try:
                await client.stop()
            finally:
                self._client = None
                self._loop = None

    @staticmethod
    def _key_from_info(controller: _ControllerKey, info: ConnectionInfo) -> _SessionKey | None:
        if not info.game_instance_id or not info.session_id:
            return None
        return _SessionKey(controller, info.game_instance_id, info.session_id, info.protocol_version)

    def _trim_sessions_locked(self) -> None:
        while len(self._sessions) > _MAX_SESSIONS:
            removable = next((key for key in self._sessions if key != self._active_session), None)
            if removable is None:  # pragma: no cover - one active v1 peer makes this unreachable
                break
            self._sessions.pop(removable, None)

    async def _on_connect(self, info: ConnectionInfo, capabilities: CapabilitySet) -> None:
        # The protocol doesn't expose the mod's internal controller UUID, so
        # mirror its lifetime with a fresh private controller identity for
        # every accepted connection. Even a buggy peer that repeats a session
        # UUID cannot make held handles from an older connection retarget.
        controller = _ControllerKey(str(uuid4()))
        self._controller_key = controller
        key = self._key_from_info(controller, info)
        if key is None:
            raise ProtocolError("connected peer omitted its protocol session identity")
        previous: _SessionKey | None
        with self._lock:
            previous = self._active_session
            if previous is not None and previous != key:
                old = self._sessions.get(previous)
                if old is not None:
                    old.reset()
            self._sessions[key] = _SessionState(key, info, capabilities, VehicleRegistry(self, key))
            self._sessions.move_to_end(key)
            self._active_session = key
            self._trim_sessions_locked()
            self._connection_ready.set()
            self._vehicle_changed.notify_all()
        if previous is not None and previous != key:
            self._control._invalidate_session(previous)
            self._fail_pending(NotConnectedError("the KSA protocol session was replaced"), previous)
        self._drain_states()
        self._queue_lifecycle(_LifecycleEvent("connect", key, info))

    async def _on_disconnect(self, info: ConnectionInfo, reason: str) -> None:
        key: _SessionKey | None = None
        was_active = False
        with self._lock:
            active = self._active_state_locked()
            if (
                active is not None
                and active.connection.game_instance_id == info.game_instance_id
                and active.connection.session_id == info.session_id
            ):
                key = active.key
            else:
                key = next(
                    (
                        candidate
                        for candidate, state in reversed(self._sessions.items())
                        if state.connection.game_instance_id == info.game_instance_id
                        and state.connection.session_id == info.session_id
                    ),
                    None,
                )
            if key is not None:
                session = self._sessions.get(key)
                if session is not None:
                    session.reset()
                if self._active_session == key:
                    self._active_session = None
                    was_active = True
                    self._connection_ready.clear()
            elif self._active_session is None:
                was_active = True
            self._vehicle_changed.notify_all()
        if was_active:
            self._drain_states()
        if key is not None:
            self._control._invalidate_session(key)
        try:
            self._queue_lifecycle(_LifecycleEvent("disconnect", key, (info, reason)))
        finally:
            if key is not None:
                self._fail_pending(NotConnectedError(reason), key)

    async def _on_latency(self, info: ConnectionInfo, round_trip_s: float) -> None:
        with self._lock:
            active = self._active_state_locked()
            if (
                active is not None
                and active.connection.game_instance_id == info.game_instance_id
                and active.connection.session_id == info.session_id
            ):
                active.transport_rtt_s = max(0.0, float(round_trip_s))

    async def _on_envelope(
        self,
        envelope: Envelope,
        validated_payload: FlightState | Catalog | None = None,
    ) -> None:
        if envelope.session_id is None:
            raise ProtocolError("post-handshake message omitted session_id")
        with self._lock:
            active = self._active_state_locked()
            if (
                active is None
                or active.connection.game_instance_id != envelope.game_instance_id
                or active.connection.session_id != envelope.session_id
            ):
                raise ProtocolError("message belongs to an inactive protocol session")
            session_key = active.key
        if envelope.request_id is not None and envelope.type in {
            "response",
            "lease.granted",
            "lease.release",
            "error",
        }:
            request_key = _PendingRequestKey.for_session(session_key, envelope.request_id)
            policy_error: ProtocolError | None = None
            with self._pending_lock:
                pending = self._pending.get(request_key)
                if pending is not None:
                    if envelope.type != "error" and envelope.type not in pending.accepted_types:
                        # Leave this waiter registered and asleep. BridgeClient
                        # policy-closes the peer, queues its exact disconnect,
                        # and only then releases unresolved pending work.
                        policy_error = ProtocolError(f"expected {sorted(pending.accepted_types)}, got {envelope.type}")
                        pending.error = policy_error
                    else:
                        # Claim a valid reply atomically. A concurrent
                        # disconnect may then fail only unresolved work.
                        self._pending.pop(request_key, None)
            if pending is not None:
                if policy_error is not None:
                    raise policy_error
                pending.envelope = envelope
                pending.event.set()
                return
            # A correlated reply with no exact pending key is late. In
            # particular, it must never be reinterpreted as an unsolicited
            # lease release after a timeout and subsequent reacquisition.
            return
        with self._lock:
            if self._active_session != session_key:
                raise ProtocolError("message belongs to an inactive protocol session")
        if envelope.type == "telemetry":
            state = (
                validated_payload
                if isinstance(validated_payload, FlightState)
                else FlightState.from_payload(envelope.payload)
            )
            runtime_capabilities = state.capabilities
            with self._lock:
                session = self._sessions.get(session_key)
                if session is None or self._active_session != session_key:
                    raise ProtocolError("telemetry belongs to an inactive protocol session")
                session.state = state
                session.state_received_monotonic_s = time.monotonic()
                session.capabilities = runtime_capabilities
                self._sync_vehicle_registry_locked(session)
                active_handle = None if state.vehicle is None else session.registry.get(state.vehicle.id)
                self._vehicle_changed.notify_all()
            active_vehicle_id = None if state.vehicle is None else state.vehicle.id
            self._control._invalidate_unusable_vehicle(
                session_key,
                active_vehicle_id,
                active_handle=active_handle,
                controllable=(state.vehicle is not None and state.vehicle.controllable),
                control_capable=runtime_capabilities.control.thrust_vector_target,
            )
            _latest_put(self._states, (session_key, state))
        elif envelope.type == "catalog":
            catalog = (
                validated_payload if isinstance(validated_payload, Catalog) else Catalog.from_payload(envelope.payload)
            )
            with self._lock:
                session = self._sessions.get(session_key)
                if session is None or self._active_session != session_key:
                    raise ProtocolError("catalog belongs to an inactive protocol session")
                session.source_catalog = catalog
                self._sync_vehicle_registry_locked(session)
                self._vehicle_changed.notify_all()
        elif envelope.type == "lease.release":
            self._control._invalidate_remote(session_key, envelope.payload.get("lease_id"))

    def _fail_pending(self, error: BaseException, session: _SessionKey | None = None) -> None:
        with self._pending_lock:
            selected = [
                (key, pending) for key, pending in self._pending.items() if session is None or key.belongs_to(session)
            ]
            for key, _ in selected:
                self._pending.pop(key, None)
        for _, pending in selected:
            if pending.error is None:
                pending.error = error
            pending.event.set()

    def _queue_lifecycle(self, event: _LifecycleEvent) -> None:
        try:
            self._lifecycle.put_nowait(event)
        except queue.Full as exc:
            fault = LifecycleOverflowError(f"KittenIO lifecycle event capacity ({_MAX_LIFECYCLE_EVENTS}) is full")
            self._signal_terminal_fault(fault)
            raise fault from exc

    def _signal_terminal_fault(self, error: BaseException) -> None:
        schedule_stop = False
        with self._terminal_lock:
            if self._terminal_error is None:
                self._terminal_error = error
            if not self._terminal_stop_scheduled:
                self._terminal_stop_scheduled = True
                schedule_stop = True
        self._stopping.set()
        if not schedule_stop:
            return
        loop, client = self._loop, self._client
        if loop is None or client is None or not loop.is_running():
            return
        try:
            running = asyncio.get_running_loop()
        except RuntimeError:
            running = None
        if running is loop:
            asyncio.create_task(client.stop(), name="kittenio-terminal-stop")
        else:
            stop_coroutine = client.stop()
            try:
                asyncio.run_coroutine_threadsafe(stop_coroutine, loop)
            except RuntimeError:
                stop_coroutine.close()

    def _raise_terminal_fault(self) -> None:
        with self._terminal_lock:
            error = self._terminal_error
        if error is not None:
            raise error

    def _drain_states(self) -> None:
        while True:
            try:
                self._states.get_nowait()
            except queue.Empty:
                return

    def _drain_lifecycle_queue(self) -> None:
        while True:
            try:
                self._lifecycle.get_nowait()
            except queue.Empty:
                return
