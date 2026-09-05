"""Canonical version-one wire envelope and strict JSON codec."""

from __future__ import annotations

import json
import math
import time
from dataclasses import dataclass
from types import MappingProxyType
from typing import Any, Mapping
from uuid import UUID

PROTOCOL_VERSION = 1
SUPPORTED_PROTOCOL_VERSIONS = (PROTOCOL_VERSION,)
MAX_MESSAGE_BYTES = 256 * 1024
MESSAGE_TYPES = frozenset(
    {
        "hello",
        "welcome",
        "telemetry",
        "catalog",
        "event",
        "lease.acquire",
        "lease.granted",
        "lease.release",
        "control",
        "request",
        "response",
        "ping",
        "pong",
        "error",
    }
)


class ProtocolError(ValueError):
    """Raised for malformed or unsupported wire data."""


def _reject_constant(value: str) -> None:
    raise ProtocolError(f"non-finite JSON number {value} is forbidden")


def _object_no_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ProtocolError(f"duplicate JSON property: {key}")
        result[key] = value
    return result


def _validate_json_value(value: Any, path: str = "payload") -> None:
    if value is None or isinstance(value, (str, bool, int)):
        return
    if isinstance(value, float):
        if not math.isfinite(value):
            raise ProtocolError(f"{path} contains a non-finite number")
        return
    if isinstance(value, Mapping):
        for key, item in value.items():
            if not isinstance(key, str):
                raise ProtocolError(f"{path} contains a non-string key")
            _validate_json_value(item, f"{path}.{key}")
        return
    if isinstance(value, (list, tuple)):
        for index, item in enumerate(value):
            _validate_json_value(item, f"{path}[{index}]")
        return
    raise ProtocolError(f"{path} contains unsupported value {type(value).__name__}")


@dataclass(frozen=True, slots=True)
class Envelope:
    version: int
    type: str
    game_instance_id: str
    session_id: str | None
    seq: int
    sent_monotonic_ms: int
    payload: Mapping[str, Any]
    request_id: str | None = None

    def __post_init__(self) -> None:
        if self.version != PROTOCOL_VERSION:
            raise ProtocolError(f"unsupported protocol version {self.version}")
        if self.type not in MESSAGE_TYPES:
            raise ProtocolError(f"unknown message type {self.type!r}")
        _validate_uuid(self.game_instance_id, "game_instance_id")
        if self.session_id is None:
            if self.type != "hello":
                raise ProtocolError("session_id may be null only in hello")
        else:
            if self.type == "hello":
                raise ProtocolError("hello must use a null session_id")
            _validate_uuid(self.session_id, "session_id")
        if isinstance(self.seq, bool) or not isinstance(self.seq, int) or self.seq < 0:
            raise ProtocolError("seq must be a non-negative integer")
        if (
            isinstance(self.sent_monotonic_ms, bool)
            or not isinstance(self.sent_monotonic_ms, int)
            or self.sent_monotonic_ms < 0
        ):
            raise ProtocolError("sent_monotonic_ms must be a non-negative integer")
        if self.request_id is not None and (not isinstance(self.request_id, str) or not self.request_id):
            raise ProtocolError("request_id must be a non-empty string when present")
        if not isinstance(self.payload, Mapping):
            raise ProtocolError("payload must be an object")
        _validate_json_value(self.payload)
        object.__setattr__(self, "payload", MappingProxyType(dict(self.payload)))

    @classmethod
    def create(
        cls,
        message_type: str,
        *,
        game_instance_id: str,
        session_id: str | None,
        seq: int,
        payload: Mapping[str, Any] | None = None,
        request_id: str | None = None,
        version: int = PROTOCOL_VERSION,
    ) -> "Envelope":
        return cls(
            version=version,
            type=message_type,
            game_instance_id=game_instance_id,
            session_id=session_id,
            seq=seq,
            sent_monotonic_ms=int(time.monotonic() * 1000.0),
            request_id=request_id,
            payload=payload or {},
        )

    def to_dict(self) -> dict[str, Any]:
        value: dict[str, Any] = {
            "version": self.version,
            "type": self.type,
            "game_instance_id": self.game_instance_id,
            "session_id": self.session_id,
            "seq": self.seq,
            "sent_monotonic_ms": self.sent_monotonic_ms,
            "payload": dict(self.payload),
        }
        if self.request_id is not None:
            value["request_id"] = self.request_id
        return value


def encode_envelope(envelope: Envelope) -> str:
    try:
        encoded = json.dumps(envelope.to_dict(), separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    except (TypeError, ValueError) as exc:
        raise ProtocolError(str(exc)) from exc
    if len(encoded.encode("utf-8")) > MAX_MESSAGE_BYTES:
        raise ProtocolError("message exceeds 256 KiB limit")
    return encoded


def _validate_uuid(value: object, name: str) -> None:
    if not isinstance(value, str):
        raise ProtocolError(f"{name} must be a canonical UUID string")
    try:
        parsed = UUID(value)
    except (ValueError, AttributeError) as exc:
        raise ProtocolError(f"{name} must be a canonical UUID string") from exc
    if str(parsed) != value:
        raise ProtocolError(f"{name} must be a lowercase canonical UUID string")
    if parsed.int == 0:
        raise ProtocolError(f"{name} must be a non-empty UUID")


def decode_envelope(data: str | bytes) -> Envelope:
    if isinstance(data, bytes):
        if len(data) > MAX_MESSAGE_BYTES:
            raise ProtocolError("message exceeds 256 KiB limit")
        try:
            data = data.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise ProtocolError("message is not valid UTF-8") from exc
    elif not isinstance(data, str):
        raise ProtocolError("WebSocket message must be text")
    if len(data.encode("utf-8")) > MAX_MESSAGE_BYTES:
        raise ProtocolError("message exceeds 256 KiB limit")
    try:
        value = json.loads(
            data,
            parse_constant=_reject_constant,
            object_pairs_hook=_object_no_duplicates,
        )
    except ProtocolError:
        raise
    except (ValueError, UnicodeError) as exc:
        raise ProtocolError(f"invalid JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise ProtocolError("envelope must be an object")
    required = {
        "version",
        "type",
        "game_instance_id",
        "session_id",
        "seq",
        "sent_monotonic_ms",
        "payload",
    }
    allowed = required | {"request_id"}
    missing = required - set(value)
    unknown = set(value) - allowed
    if missing:
        raise ProtocolError(f"missing envelope properties: {', '.join(sorted(missing))}")
    if unknown:
        raise ProtocolError(f"unknown envelope properties: {', '.join(sorted(unknown))}")
    try:
        return Envelope(
            version=value["version"],
            type=value["type"],
            game_instance_id=value["game_instance_id"],
            session_id=value["session_id"],
            seq=value["seq"],
            sent_monotonic_ms=value["sent_monotonic_ms"],
            request_id=value.get("request_id"),
            payload=value["payload"],
        )
    except (TypeError, KeyError) as exc:
        raise ProtocolError(str(exc)) from exc
