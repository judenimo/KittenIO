"""Acknowledged, game-global time-warp control."""

from __future__ import annotations

import math
from typing import TYPE_CHECKING, Any
from uuid import UUID, uuid4

from ..wire.common import CommandError, CommandResult
from ..wire.models import UnsupportedCapabilityError

if TYPE_CHECKING:
    from .runtime import KittenRuntime


class TimeWarpError(RuntimeError):
    """Raised when a local time-warp request or response is malformed."""


class TimeWarp:
    """Control KSA's persistent, game-global simulation speed.

    Success means a later authoritative game frame confirmed the requested
    rate. Warp belongs to KSA: disconnect or SDK shutdown does not cancel it.
    """

    def __init__(self, runtime: "KittenRuntime") -> None:
        self._runtime = runtime

    @property
    def available_rates(self) -> tuple[float, ...]:
        return self._runtime.capabilities.time_warp.available_rates

    @property
    def current_rate(self) -> float | None:
        state = self._runtime.data
        return None if state is None else state.simulation_speed

    def set_rate(
        self,
        rate: float,
        *,
        command_id: str | None = None,
        timeout: float | None = None,
    ) -> CommandResult:
        self._require_time_warp()
        if isinstance(rate, bool) or not isinstance(rate, (int, float)):
            raise TypeError("rate must be numeric")
        value = float(rate)
        if not math.isfinite(value) or value < 0.0:
            raise ValueError("rate must be finite and non-negative")
        rates = self.available_rates
        if not any(abs(value - supported) <= 0.0001 for supported in rates):
            raise ValueError(f"unsupported warp rate {value:g}; available rates are {rates}")
        return self._command("time_warp.set_rate", {"rate": value}, command_id=command_id, timeout=timeout)

    def increase(self, *, command_id: str | None = None, timeout: float | None = None) -> CommandResult:
        return self._command("time_warp.increase", {}, command_id=command_id, timeout=timeout)

    def decrease(self, *, command_id: str | None = None, timeout: float | None = None) -> CommandResult:
        return self._command("time_warp.decrease", {}, command_id=command_id, timeout=timeout)

    def cancel(self, *, command_id: str | None = None, timeout: float | None = None) -> CommandResult:
        """Request KSA's native reset action, returning speed to 1x."""

        return self._command("time_warp.cancel", {}, command_id=command_id, timeout=timeout)

    def _command(
        self,
        method: str,
        params: dict[str, object],
        *,
        command_id: str | None,
        timeout: float | None,
    ) -> CommandResult:
        session, _ = self._require_time_warp()

        envelope = self._runtime._request_in_session(
            session,
            "request",
            {
                "method": method,
                "command_id": _canonical_command_id(command_id),
                "params": params,
            },
            timeout=timeout,
            accepted_types={"response"},
        )
        expected_rate = params.get("rate") if method == "time_warp.set_rate" else None
        if method == "time_warp.cancel":
            expected_rate = 1.0
        return _decode_command_result(envelope.payload, expected_rate=expected_rate)

    def _require_time_warp(self):
        session, connection = self._runtime._require_active_session()
        capability = self._runtime.capabilities.time_warp
        if not capability.control:
            reason = capability.unavailable_reason
            suffix = "" if reason is None else f": {reason}"
            raise UnsupportedCapabilityError(f"time-warp control is unavailable{suffix}")
        return session, connection


def _canonical_command_id(value: str | None) -> str:
    if value is None:
        return str(uuid4())
    try:
        parsed = UUID(value) if isinstance(value, str) else None
    except ValueError as exc:
        raise TimeWarpError("command_id must be a canonical non-zero UUID") from exc
    if parsed is None or parsed.int == 0 or str(parsed) != value:
        raise TimeWarpError("command_id must be a canonical non-zero UUID")
    return value


def _decode_command_result(payload: dict[str, Any], *, expected_rate: object | None = None) -> CommandResult:
    if set(payload) - {"ok", "result", "error"}:
        raise TimeWarpError("time-warp response contains unknown properties")
    ok = payload.get("ok")
    if not isinstance(ok, bool):
        raise TimeWarpError("time-warp response omitted boolean ok")
    if ok:
        if "result" not in payload or "error" in payload:
            raise TimeWarpError("invalid successful time-warp response")
        result = payload["result"]
        if not isinstance(result, dict):
            raise TimeWarpError("time-warp response result must be an object")
        if set(result) != {"rate", "previous_rate", "confirmed_frame"}:
            raise TimeWarpError("time-warp result requires exactly rate, previous_rate, and confirmed_frame")
        rate, previous_rate = result["rate"], result["previous_rate"]
        confirmed_frame = result["confirmed_frame"]
        if any(
            isinstance(value, bool)
            or not isinstance(value, (int, float))
            or not math.isfinite(float(value))
            or float(value) < 0.0
            for value in (rate, previous_rate)
        ):
            raise TimeWarpError("time-warp result rates must be finite and non-negative")
        if isinstance(confirmed_frame, bool) or not isinstance(confirmed_frame, int) or confirmed_frame < 0:
            raise TimeWarpError("time-warp confirmed_frame must be a non-negative integer")
        if expected_rate is not None and abs(float(rate) - float(expected_rate)) > 0.0001:
            raise TimeWarpError("time-warp response confirmed an unexpected rate")
        return CommandResult(ok=True, result=result)
    if "error" not in payload or "result" in payload:
        raise TimeWarpError("invalid failed time-warp response")
    error = payload["error"]
    if not isinstance(error, dict) or set(error) != {"code", "message"}:
        raise TimeWarpError("time-warp response error requires exactly code and message")
    code, message = error.get("code"), error.get("message")
    if not isinstance(code, str) or not isinstance(message, str):
        raise TimeWarpError("time-warp response error requires string code and message")
    return CommandResult(ok=False, error=CommandError(code, message))
