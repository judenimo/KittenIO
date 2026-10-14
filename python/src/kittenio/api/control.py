"""Thread-safe flight-control facade used by mission programs."""

from __future__ import annotations

import threading
from collections import OrderedDict
from concurrent.futures import CancelledError
from dataclasses import dataclass, replace
from typing import TYPE_CHECKING, Any
from uuid import UUID, uuid4

from websockets.exceptions import ConnectionClosed

from ..transport.client import NotConnectedError
from ..wire.common import CommandError, CommandResult, Vector3
from ..wire.models import ThrustVectorControl, UnsupportedCapabilityError

if TYPE_CHECKING:
    from ..runtime.engine import KittenRuntime


_MAX_LEASE_RECORDS = 64


class ControlError(RuntimeError):
    """Raised when a control operation cannot safely be completed."""


@dataclass(frozen=True, slots=True)
class _FallbackSessionKey:
    """Exact context for test/dummy runtimes without the private store API."""

    controller: object
    game_instance_id: str
    session_id: str
    protocol_version: int = 1


@dataclass(frozen=True, slots=True)
class _LeaseAcquisition:
    session: object
    vehicle_id: str
    generation: int
    token: str
    handle: object | None = None


@dataclass(frozen=True, slots=True)
class _LeaseContext:
    session: object
    lease_id: str
    vehicle_id: str
    generation: int
    handle: object | None = None


def _session_identity(session: object) -> tuple[object, str, str]:
    try:
        return (
            getattr(session, "controller"),
            getattr(session, "game_instance_id"),
            getattr(session, "session_id"),
        )
    except AttributeError as exc:  # pragma: no cover - internal misuse guard
        raise ControlError("invalid protocol session context") from exc


class ControlClient:
    def __init__(self, runtime: "KittenRuntime") -> None:
        self._runtime = runtime
        self._lock = threading.RLock()
        self._leases: OrderedDict[_LeaseContext, None] = OrderedDict()
        self._released_leases: OrderedDict[tuple[object, str], None] = OrderedDict()
        self._active_lease: _LeaseContext | None = None
        self._acquiring: _LeaseAcquisition | None = None
        self._lease_id: str | None = None
        self._vehicle_id: str | None = None
        self._control_seq = 0
        self._override: ThrustVectorControl | None = None
        self._generation = 0

    @property
    def lease_active(self) -> bool:
        with self._lock:
            return self._active_lease is not None

    @property
    def override(self) -> ThrustVectorControl | None:
        with self._lock:
            return self._override

    def _current_session(self) -> object:
        resolver = getattr(self._runtime, "_require_active_session", None)
        if callable(resolver):
            session, _ = resolver()
            return session
        connection = self._runtime.connection_info
        if not connection.connected or not connection.game_instance_id or not connection.session_id:
            raise NotConnectedError("the KSA mod is not connected")
        controller = getattr(self._runtime, "_controller_key", id(self._runtime))
        return _FallbackSessionKey(controller, connection.game_instance_id, connection.session_id)

    def _session_is_current(self, expected: object) -> bool:
        try:
            return self._current_session() == expected
        except NotConnectedError:
            return False

    def _control_context_is_current(
        self,
        session: object,
        vehicle_id: str,
        handle: object | None = None,
    ) -> bool:
        validator = getattr(self._runtime, "_vehicle_control_context_is_current", None)
        if callable(validator):
            if handle is not None:
                return bool(validator(session, vehicle_id, handle))
            return bool(validator(session, vehicle_id))
        return self._session_is_current(session)

    def _request_in_session(
        self,
        session: object,
        message_type: str,
        payload: dict[str, Any],
        *,
        timeout: float | None,
        accepted_types: set[str],
    ):
        sender = getattr(self._runtime, "_request_in_session", None)
        if callable(sender):
            return sender(
                session,
                message_type,
                payload,
                timeout=timeout,
                accepted_types=accepted_types,
            )
        if not self._session_is_current(session):
            raise NotConnectedError("the KSA protocol session changed before request")
        return self._runtime.request(
            message_type,
            payload,
            timeout=timeout,
            accepted_types=accepted_types,
        )

    def _send_in_session(self, session: object, message_type: str, payload: dict[str, Any]) -> None:
        sender = getattr(self._runtime, "_send_in_session", None)
        if callable(sender):
            sender(session, message_type, payload)
            return
        if not self._session_is_current(session):
            raise NotConnectedError("the KSA protocol session changed before send")
        self._runtime.send(message_type, payload)

    def acquire(
        self,
        vehicle_id: str | None = None,
        *,
        timeout: float | None = None,
        _expected_session: object | None = None,
        _expected_handle: object | None = None,
    ) -> str:
        session = self._current_session()
        if _expected_session is not None and session != _expected_session:
            raise ControlError("the vehicle handle belongs to an inactive protocol session")
        state = getattr(self._runtime, "data", getattr(self._runtime, "state", None))
        if vehicle_id is None:
            if state is None or state.vehicle is None:
                raise ControlError("there is no active vehicle")
            vehicle_id = state.vehicle.id
        if not self._control_context_is_current(session, vehicle_id, _expected_handle):
            raise ControlError("only the current active, controllable vehicle can acquire control")
        with self._lock:
            if self._active_lease is not None or self._acquiring is not None:
                raise ControlError("a control lease is already active or being acquired")
            acquisition = _LeaseAcquisition(
                session=session,
                vehicle_id=vehicle_id,
                generation=self._generation,
                token=str(uuid4()),
                handle=_expected_handle,
            )
            self._acquiring = acquisition
        try:
            envelope = self._request_in_session(
                session,
                "lease.acquire",
                {"vehicle_id": vehicle_id},
                timeout=timeout,
                accepted_types={"lease.granted"},
            )
            required = {"lease_id", "vehicle_id", "timeout_ms"}
            actual = set(envelope.payload)
            if actual != required:
                missing = sorted(required - actual)
                unknown = sorted(actual - required)
                details = []
                if missing:
                    details.append(f"missing {', '.join(missing)}")
                if unknown:
                    details.append(f"unknown {', '.join(unknown)}")
                raise ControlError(f"invalid lease.granted payload ({'; '.join(details)})")
            lease_id = envelope.payload["lease_id"]
            granted_vehicle_id = envelope.payload["vehicle_id"]
            timeout_ms = envelope.payload["timeout_ms"]
            try:
                parsed_lease_id = UUID(lease_id) if isinstance(lease_id, str) else None
            except ValueError as exc:
                raise ControlError("lease.granted lease_id must be a canonical non-zero UUID") from exc
            if parsed_lease_id is None or parsed_lease_id.int == 0 or str(parsed_lease_id) != lease_id:
                raise ControlError("lease.granted lease_id must be a canonical non-zero UUID")
            if granted_vehicle_id != vehicle_id:
                raise ControlError("lease.granted vehicle_id does not match the requested vehicle")
            if isinstance(timeout_ms, bool) or not isinstance(timeout_ms, int) or not 250 <= timeout_ms <= 750:
                raise ControlError("lease.granted timeout_ms must be an integer from 250 through 750")
            if envelope.session_id != _session_identity(session)[2]:
                raise ControlError("the connection changed while acquiring the control lease")
            with self._lock:
                released_key = (session, lease_id)
                if (
                    self._acquiring != acquisition
                    or self._generation != acquisition.generation
                    or self._active_lease is not None
                    or not self._control_context_is_current(session, vehicle_id, acquisition.handle)
                ):
                    raise ControlError("the connection changed while acquiring the control lease")
                if released_key in self._released_leases:
                    self._released_leases.pop(released_key, None)
                    raise ControlError("the control lease was released while being acquired")
                context = _LeaseContext(
                    session=session,
                    lease_id=lease_id,
                    vehicle_id=vehicle_id,
                    generation=self._generation,
                    handle=acquisition.handle,
                )
                self._register_lease_locked(context)
                self._active_lease = context
                self._lease_id = lease_id
                self._vehicle_id = vehicle_id
                self._control_seq = 0
                self._override = None
            return lease_id
        finally:
            with self._lock:
                if self._acquiring == acquisition:
                    self._acquiring = None

    def release(self, *, timeout: float | None = None) -> None:
        with self._lock:
            context = self._active_lease
        if context is None:
            return
        self._release_context(context, timeout=timeout)

    def _release_context(self, context: _LeaseContext, *, timeout: float | None) -> None:
        try:
            envelope = self._request_in_session(
                context.session,
                "lease.release",
                {"lease_id": context.lease_id},
                timeout=timeout,
                accepted_types={"lease.release"},
            )
            actual = set(envelope.payload)
            if actual != {"lease_id", "reason"}:
                raise ControlError("invalid lease.release payload")
            if envelope.payload["lease_id"] != context.lease_id:
                raise ControlError("lease.release acknowledged a different lease")
            reason = envelope.payload["reason"]
            if reason is not None and not isinstance(reason, str):
                raise ControlError("lease.release reason must be a string or null")
        finally:
            self._invalidate_context(context)

    def _acquire_for(
        self,
        session: object,
        vehicle_id: str,
        handle: object,
        *,
        timeout: float | None,
    ) -> str:
        return self.acquire(
            vehicle_id,
            timeout=timeout,
            _expected_session=session,
            _expected_handle=handle,
        )

    def _release_for(
        self,
        session: object,
        vehicle_id: str,
        handle: object,
        *,
        timeout: float | None,
    ) -> None:
        with self._lock:
            context = self._active_lease
            if (
                context is None
                or context.session != session
                or context.vehicle_id != vehicle_id
                or context.handle is not handle
            ):
                return
        self._release_context(context, timeout=timeout)

    def _lease_active_for(
        self,
        session: object,
        vehicle_id: str,
        handle: object | None = None,
    ) -> bool:
        with self._lock:
            context = self._active_lease
            return (
                context is not None
                and context.session == session
                and context.vehicle_id == vehicle_id
                and (handle is None or context.handle is handle)
            )

    def _lease_id_for(
        self,
        session: object,
        vehicle_id: str,
        handle: object | None = None,
    ) -> str | None:
        with self._lock:
            context = self._active_lease
            if (
                context is None
                or context.session != session
                or context.vehicle_id != vehicle_id
                or (handle is not None and context.handle is not handle)
            ):
                return None
            return context.lease_id

    def _override_for(
        self,
        session: object,
        vehicle_id: str,
        handle: object | None = None,
    ) -> ThrustVectorControl | None:
        with self._lock:
            context = self._active_lease
            if (
                context is None
                or context.session != session
                or context.vehicle_id != vehicle_id
                or (handle is not None and context.handle is not handle)
            ):
                return None
            return self._override

    def _call_for(
        self,
        session: object,
        vehicle_id: str,
        handle: object,
        method: str,
        *args: object,
        **kwargs: object,
    ) -> object:
        if method == "set_engine":
            value = args[0]
            if value is not None and not isinstance(value, bool):
                raise TypeError("engine_on must be bool or None")
            self._update_for(session, vehicle_id, handle, engine_on=value)
            return None
        if method == "set_throttle":
            self._update_for(session, vehicle_id, handle, throttle=args[0])
            return None
        if method == "set_thrust_vector":
            direction = args[0]
            if not isinstance(direction, Vector3):
                try:
                    direction = Vector3(*direction)
                except (TypeError, ValueError) as exc:
                    raise ControlError("direction_cci must be Vector3 or a three-number iterable") from exc
            state = getattr(self._runtime, "data", None)
            capabilities = getattr(state, "capabilities", None)
            control_capabilities = getattr(capabilities, "control", None)
            if not getattr(control_capabilities, "thrust_vector_target", False):
                raise UnsupportedCapabilityError("thrust_vector_target is unavailable in this session")
            with self._lock:
                context = self._require_exact_context_locked(session, vehicle_id, handle)
                try:
                    self._override = ThrustVectorControl(
                        direction,
                        engine_on=kwargs.get("engine_on"),
                        throttle=kwargs.get("throttle"),
                    )
                except (TypeError, ValueError) as exc:
                    raise ControlError(str(exc)) from exc
                payload = self._prepare_current_locked(context)
            self._send_in_session(context.session, "control", payload)
            return None
        if method == "clear_overrides":
            with self._lock:
                context = self._require_exact_context_locked(session, vehicle_id, handle)
                if self._override is None:
                    raise ControlError("set a thrust-vector target before clearing overrides")
                self._override = replace(self._override, engine_on=None, throttle=None)
                payload = self._prepare_current_locked(context)
            self._send_in_session(context.session, "control", payload)
            return None
        if method in {"stage", "set_target"}:
            with self._lock:
                context = self._require_exact_context_locked(session, vehicle_id, handle)
                params = {
                    "lease_id": context.lease_id,
                    "vehicle_id": context.vehicle_id,
                }
            if method == "stage":
                return self._command(
                    context,
                    "activate_next_sequence",
                    params,
                    command_id=kwargs.get("command_id"),
                    timeout=kwargs.get("timeout"),
                )
            target_id = args[0]
            if not isinstance(target_id, str) or not target_id:
                raise ControlError("target_id must be a non-empty catalog ID")
            return self._command(
                context,
                "set_target",
                {**params, "target_id": target_id},
                command_id=kwargs.get("command_id"),
                timeout=kwargs.get("timeout"),
            )
        raise ControlError(f"unsupported vehicle control operation {method!r}")

    def _update_for(
        self,
        session: object,
        vehicle_id: str,
        handle: object,
        **changes: object,
    ) -> None:
        with self._lock:
            context = self._require_exact_context_locked(session, vehicle_id, handle)
            try:
                if self._override is None:
                    raise ControlError("set_thrust_vector must initialize a control frame first")
                self._override = replace(self._override, **changes)
            except (TypeError, ValueError) as exc:
                raise ControlError(str(exc)) from exc
            payload = self._prepare_current_locked(context)
        self._send_in_session(context.session, "control", payload)

    def _require_exact_context_locked(
        self,
        session: object,
        vehicle_id: str,
        handle: object | None = None,
    ) -> _LeaseContext:
        context = self._require_lease_context_locked()
        if (
            context.session != session
            or context.vehicle_id != vehicle_id
            or (handle is not None and context.handle is not handle)
            or not self._control_context_is_current(session, vehicle_id, context.handle)
        ):
            raise ControlError("the control lease belongs to a different vehicle handle")
        return context

    def set_engine(self, engine_on: bool | None) -> None:
        if engine_on is not None and not isinstance(engine_on, bool):
            raise TypeError("engine_on must be bool or None")
        self._update(engine_on=engine_on)

    def set_throttle(self, throttle: float | None) -> None:
        self._update(throttle=throttle)

    def set_thrust_vector(
        self,
        direction_cci: Vector3 | tuple[float, float, float],
        *,
        engine_on: bool | None = None,
        throttle: float | None = None,
    ) -> None:
        if not isinstance(direction_cci, Vector3):
            try:
                direction_cci = Vector3(*direction_cci)
            except (TypeError, ValueError) as exc:
                raise ControlError("direction_cci must be Vector3 or a three-number tuple") from exc
        with self._lock:
            context = self._require_lease_context_locked()
            state = getattr(self._runtime, "data", None)
            control_capabilities = getattr(getattr(state, "capabilities", None), "control", None)
            if not getattr(control_capabilities, "thrust_vector_target", False):
                raise UnsupportedCapabilityError("thrust_vector_target is unavailable in this session")
            try:
                self._override = ThrustVectorControl(direction_cci, engine_on, throttle)
            except (TypeError, ValueError) as exc:
                raise ControlError(str(exc)) from exc
            payload = self._prepare_current_locked(context)
        self._send_in_session(context.session, "control", payload)

    def clear_overrides(self) -> None:
        with self._lock:
            context = self._require_lease_context_locked()
            if self._override is None:
                raise ControlError("set a thrust-vector target before clearing overrides")
            self._override = replace(self._override, engine_on=None, throttle=None)
            payload = self._prepare_current_locked(context)
        self._send_in_session(context.session, "control", payload)

    def stage(self, *, command_id: str | None = None, timeout: float | None = None) -> CommandResult:
        context, params = self._command_context()
        return self._command(
            context,
            "activate_next_sequence",
            params,
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
        if not isinstance(target_id, str) or not target_id:
            raise ControlError("target_id must be a non-empty catalog ID")
        context, params = self._command_context()
        return self._command(
            context,
            "set_target",
            {**params, "target_id": target_id},
            command_id=command_id,
            timeout=timeout,
        )

    def _command(
        self,
        context: _LeaseContext,
        method: str,
        params: dict[str, object],
        *,
        command_id: str | None,
        timeout: float | None,
    ) -> CommandResult:
        if command_id is None:
            command_id = str(uuid4())
        else:
            try:
                parsed_command_id = UUID(command_id) if isinstance(command_id, str) else None
            except ValueError as exc:
                raise ControlError("command_id must be a canonical non-zero UUID") from exc
            if parsed_command_id is None or parsed_command_id.int == 0 or str(parsed_command_id) != command_id:
                raise ControlError("command_id must be a canonical non-zero UUID")
        envelope = self._request_in_session(
            context.session,
            "request",
            {"method": method, "command_id": command_id, "params": params},
            timeout=timeout,
            accepted_types={"response"},
        )
        allowed = {"ok", "result", "error"}
        unknown = set(envelope.payload) - allowed
        if unknown:
            raise ControlError(f"response has unknown properties: {', '.join(sorted(unknown))}")
        ok = envelope.payload.get("ok")
        if not isinstance(ok, bool):
            raise ControlError("response omitted boolean ok")
        if ok and "result" not in envelope.payload:
            raise ControlError("successful response omitted result")
        if not ok and "error" not in envelope.payload:
            raise ControlError("failed response omitted error")
        if ok and "error" in envelope.payload:
            raise ControlError("successful response must not contain error")
        if not ok and "result" in envelope.payload:
            raise ControlError("failed response must not contain result")
        result = envelope.payload.get("result", {})
        if not isinstance(result, dict):
            raise ControlError("response result must be an object")
        error_value = envelope.payload.get("error")
        error: CommandError | None = None
        if not ok and error_value is None:
            raise ControlError("failed response error must be an object")
        if error_value is not None:
            if not isinstance(error_value, dict):
                raise ControlError("response error must be an object or null")
            if set(error_value) != {"code", "message"}:
                raise ControlError("response error requires exactly code and message")
            code, message = error_value.get("code"), error_value.get("message")
            if not isinstance(code, str) or not isinstance(message, str):
                raise ControlError("response error requires string code and message")
            error = CommandError(code, message)
        return CommandResult(ok=ok, result=result, error=error)

    def _command_context(self) -> tuple[_LeaseContext, dict[str, str]]:
        with self._lock:
            context = self._require_lease_context_locked()
            return context, {
                "lease_id": context.lease_id,
                "vehicle_id": context.vehicle_id,
            }

    def _update(self, **changes: object) -> None:
        with self._lock:
            context = self._require_lease_context_locked()
            try:
                if self._override is None:
                    raise ControlError("set_thrust_vector must initialize a control frame first")
                self._override = replace(self._override, **changes)
            except (TypeError, ValueError) as exc:
                raise ControlError(str(exc)) from exc
            payload = self._prepare_current_locked(context)
        self._send_in_session(context.session, "control", payload)

    def _prepare_current_locked(self, context: _LeaseContext) -> dict[str, Any]:
        if self._active_lease != context:
            raise ControlError("control lease changed before send")
        sequence = self._control_seq
        self._control_seq += 1
        if self._override is None:
            raise ControlError("set_thrust_vector must initialize a control frame before renewal")
        return self._override.to_payload(lease_id=context.lease_id, control_seq=sequence)

    def _send_current(self) -> _LeaseContext:
        with self._lock:
            context = self._require_lease_context_locked()
            payload = self._prepare_current_locked(context)
        self._send_in_session(context.session, "control", payload)
        return context

    def _refresh(self) -> None:
        with self._lock:
            context = self._active_lease
        if context is None:
            return
        if self._override is None:
            return
        if not self._control_context_is_current(context.session, context.vehicle_id, context.handle):
            self._invalidate_context(context)
            return
        try:
            with self._lock:
                payload = self._prepare_current_locked(context)
            self._send_in_session(context.session, "control", payload)
        except ControlError:
            # An unsolicited lease.release or disconnect may race the user
            # thread between the initial check and the send. That is a normal
            # dead-man transition, not a mission callback failure.
            with self._lock:
                if self._active_lease == context:
                    raise
        except (NotConnectedError, ConnectionClosed, TimeoutError, CancelledError, OSError):
            self._invalidate_context(context)
        except RuntimeError as exc:
            # Runtime overload/send-budget failures are also loss of renewal,
            # but unrelated callback errors must remain visible.
            from ..runtime.engine import RequestTimeoutError, RuntimeOverloadError

            if isinstance(exc, (RequestTimeoutError, RuntimeOverloadError)):
                self._invalidate_context(context)
            else:
                raise

    def _require_lease_context_locked(self) -> _LeaseContext:
        if self._active_lease is None:
            raise ControlError("acquire a control lease first")
        return self._active_lease

    def _register_lease_locked(self, context: _LeaseContext) -> None:
        self._leases[context] = None
        self._leases.move_to_end(context)
        while len(self._leases) > _MAX_LEASE_RECORDS:
            oldest = next(
                (candidate for candidate in self._leases if candidate != self._active_lease),
                None,
            )
            if oldest is None:  # pragma: no cover - only one lease may be active
                break
            self._leases.pop(oldest, None)

    def _deactivate_locked(self, context: _LeaseContext) -> bool:
        if self._active_lease != context:
            return False
        self._generation += 1
        self._active_lease = None
        self._lease_id = None
        self._vehicle_id = None
        self._control_seq = 0
        self._override = None
        return True

    def _invalidate_context(self, context: _LeaseContext) -> None:
        with self._lock:
            self._deactivate_locked(context)

    def _invalidate_session(self, session: object) -> None:
        with self._lock:
            for context in tuple(self._leases):
                if context.session == session:
                    self._leases.pop(context, None)
            for key in tuple(self._released_leases):
                if key[0] == session:
                    self._released_leases.pop(key, None)
            if self._acquiring is not None and self._acquiring.session == session:
                self._generation += 1
                self._acquiring = None
            if self._active_lease is not None and self._active_lease.session == session:
                self._deactivate_locked(self._active_lease)

    def _invalidate_unusable_vehicle(
        self,
        session: object,
        active_vehicle_id: str | None,
        *,
        active_handle: object | None = None,
        controllable: bool,
        control_capable: bool,
    ) -> None:
        with self._lock:
            acquiring = self._acquiring
            if (
                acquiring is not None
                and acquiring.session == session
                and (
                    acquiring.vehicle_id != active_vehicle_id
                    or (acquiring.handle is not None and acquiring.handle is not active_handle)
                    or not controllable
                    or not control_capable
                )
            ):
                self._generation += 1
                self._acquiring = None

            active = self._active_lease
            if (
                active is not None
                and active.session == session
                and (
                    active.vehicle_id != active_vehicle_id
                    or (active.handle is not None and active.handle is not active_handle)
                    or not controllable
                    or not control_capable
                )
            ):
                self._deactivate_locked(active)

    def _invalidate_remote(self, session: object, lease_id: object) -> None:
        if not isinstance(lease_id, str):
            return
        with self._lock:
            released_key = (session, lease_id)
            self._released_leases[released_key] = None
            self._released_leases.move_to_end(released_key)
            while len(self._released_leases) > _MAX_LEASE_RECORDS:
                self._released_leases.popitem(last=False)
            context = self._active_lease
            if context is not None and context.session == session and context.lease_id == lease_id:
                self._deactivate_locked(context)

    def _invalidate(self) -> None:
        with self._lock:
            self._generation += 1
            self._acquiring = None
            self._active_lease = None
            self._leases.clear()
            self._released_leases.clear()
            self._lease_id = None
            self._vehicle_id = None
            self._control_seq = 0
            self._override = None
