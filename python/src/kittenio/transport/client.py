"""Authenticated reconnecting WebSocket client used by the Python runtime."""

from __future__ import annotations

import asyncio
import inspect
import random
import time
from collections import defaultdict
from collections.abc import Awaitable, Callable, Mapping
from dataclasses import dataclass, field, replace
from typing import Any
from uuid import UUID, uuid4

from ..wire.common import Catalog, ConnectionInfo, ModelError
from ..wire.models import CapabilitySet, FlightState
from ..wire.protocol import (
    MAX_MESSAGE_BYTES,
    PROTOCOL_VERSION,
    SUPPORTED_PROTOCOL_VERSIONS,
    Envelope,
    ProtocolError,
    decode_envelope,
    encode_envelope,
)
from .config import Config

SDK_VERSION = "0.1.0"
_HELLO_TIMEOUT_S = 5.0
_HELLO_FIELDS = frozenset(
    {
        "protocol",
        "supported_versions",
        "mod_version",
        "game_version",
        "starmap_version",
        "capabilities",
        "units",
        "coordinate_frames",
    }
)
_MOD_TO_SDK_TYPES = frozenset(
    {
        "telemetry",
        "catalog",
        "event",
        "lease.granted",
        "lease.release",
        "response",
        "ping",
        "pong",
        "error",
    }
)


class ServerError(RuntimeError):
    """Base class for bridge transport failures."""


class NotConnectedError(ServerError):
    """Raised when a message is sent without an authenticated game peer."""


class TerminalConnectionError(ServerError):
    """A deterministic peer/policy failure which must not be retried."""


ValidatedPayload = FlightState | Catalog | None
EnvelopeHandler = Callable[[Envelope, ValidatedPayload], Awaitable[None] | None]
ConnectionHandler = Callable[[ConnectionInfo, CapabilitySet], Awaitable[None] | None]
DisconnectHandler = Callable[[ConnectionInfo, str], Awaitable[None] | None]
LatencyHandler = Callable[[ConnectionInfo, float], Awaitable[None] | None]


async def _invoke(callback: Callable[..., Any] | None, *args: Any) -> None:
    if callback is None:
        return
    result = callback(*args)
    if inspect.isawaitable(result):
        await result


@dataclass(frozen=True, slots=True)
class _HelloPayload:
    mod_version: str
    game_version: str
    starmap_version: str
    capabilities: CapabilitySet
    supported_versions: tuple[int, ...]
    coordinate_frames: tuple[str, ...]

    @classmethod
    def from_payload(cls, value: Mapping[str, Any]) -> "_HelloPayload":
        _exact_payload(value, _HELLO_FIELDS, "hello")
        if value.get("protocol") != "kittenio":
            raise ProtocolError("hello protocol must be kittenio")
        versions = value.get("supported_versions")
        if (
            not isinstance(versions, list)
            or any(isinstance(item, bool) or not isinstance(item, int) for item in versions)
            or tuple(versions) != SUPPORTED_PROTOCOL_VERSIONS
        ):
            raise ProtocolError("game peer must advertise only protocol version 1")
        version_strings: dict[str, str] = {}
        for name in ("mod_version", "game_version", "starmap_version"):
            item = value.get(name)
            if not isinstance(item, str) or not item:
                raise ProtocolError(f"hello {name} must be a non-empty string")
            version_strings[name] = item
        if value.get("units") != "SI":
            raise ProtocolError("hello units must be SI")
        frames = value.get("coordinate_frames")
        if not isinstance(frames, list) or any(not isinstance(frame, str) for frame in frames):
            raise ProtocolError("hello coordinate_frames must be a string array")
        return cls(
            mod_version=version_strings["mod_version"],
            game_version=version_strings["game_version"],
            starmap_version=version_strings["starmap_version"],
            capabilities=CapabilitySet.from_dict(value.get("capabilities", {})),
            supported_versions=tuple(versions),
            coordinate_frames=tuple(frames),
        )


def _exact_payload(payload: Mapping[str, Any], fields: frozenset[str], message_type: str) -> None:
    actual = set(payload)
    if actual == fields:
        return
    missing = fields - actual
    unknown = actual - fields
    details: list[str] = []
    if missing:
        details.append(f"missing {', '.join(sorted(missing))}")
    if unknown:
        details.append(f"unknown {', '.join(sorted(unknown))}")
    raise ProtocolError(f"{message_type} payload has {'; '.join(details)}")


def _nonempty_string(value: object, name: str) -> str:
    if not isinstance(value, str) or not value:
        raise ProtocolError(f"{name} must be a non-empty string")
    return value


def _canonical_uuid(value: object, name: str) -> str:
    if not isinstance(value, str):
        raise ProtocolError(f"{name} must be a canonical non-zero UUID")
    try:
        parsed = UUID(value)
    except (ValueError, AttributeError) as exc:
        raise ProtocolError(f"{name} must be a canonical non-zero UUID") from exc
    if parsed.int == 0 or str(parsed) != value:
        raise ProtocolError(f"{name} must be a lowercase canonical non-zero UUID")
    return value


def _validate_mod_payload(envelope: Envelope) -> ValidatedPayload:
    """Validate every payload shape accepted from an authenticated game peer."""

    payload = envelope.payload
    if envelope.type == "telemetry":
        return FlightState.from_payload(payload)
    elif envelope.type == "catalog":
        return Catalog.from_payload(payload)
    elif envelope.type == "event":
        _exact_payload(payload, frozenset({"name", "data"}), "event")
        _nonempty_string(payload["name"], "event.name")
        if not isinstance(payload["data"], Mapping):
            raise ProtocolError("event.data must be an object")
    elif envelope.type == "lease.granted":
        _exact_payload(
            payload,
            frozenset({"lease_id", "vehicle_id", "timeout_ms"}),
            "lease.granted",
        )
        if envelope.request_id is None:
            raise ProtocolError("lease.granted requires request_id")
        _canonical_uuid(payload["lease_id"], "lease.granted.lease_id")
        _nonempty_string(payload["vehicle_id"], "lease.granted.vehicle_id")
        timeout_ms = payload["timeout_ms"]
        if isinstance(timeout_ms, bool) or not isinstance(timeout_ms, int) or not 250 <= timeout_ms <= 750:
            raise ProtocolError("lease.granted.timeout_ms must be an integer from 250 through 750")
    elif envelope.type == "lease.release":
        _exact_payload(payload, frozenset({"lease_id", "reason"}), "lease.release")
        _canonical_uuid(payload["lease_id"], "lease.release.lease_id")
        if payload["reason"] is not None and not isinstance(payload["reason"], str):
            raise ProtocolError("lease.release.reason must be a string or null")
    elif envelope.type == "response":
        if envelope.request_id is None:
            raise ProtocolError("response requires request_id")
        ok = payload.get("ok")
        if not isinstance(ok, bool):
            raise ProtocolError("response.ok must be a boolean")
        expected = frozenset({"ok", "result"}) if ok else frozenset({"ok", "error"})
        _exact_payload(payload, expected, "response")
        if ok:
            if not isinstance(payload["result"], Mapping):
                raise ProtocolError("response.result must be an object")
        else:
            error = payload["error"]
            if not isinstance(error, Mapping):
                raise ProtocolError("response.error must be an object")
            _exact_payload(error, frozenset({"code", "message"}), "response.error")
            _nonempty_string(error["code"], "response.error.code")
            _nonempty_string(error["message"], "response.error.message")
    elif envelope.type in {"ping", "pong"}:
        _exact_payload(payload, frozenset({"nonce"}), envelope.type)
        _nonempty_string(payload["nonce"], f"{envelope.type}.nonce")
    elif envelope.type == "error":
        _exact_payload(payload, frozenset({"code", "message"}), "error")
        _nonempty_string(payload["code"], "error.code")
        _nonempty_string(payload["message"], "error.message")
    return None


@dataclass(slots=True)
class _PeerSession:
    """All state whose lifetime is exactly one WebSocket peer."""

    peer: Any
    connection: ConnectionInfo = field(default_factory=lambda: ConnectionInfo(False))
    outbound_sequences: defaultdict[str, int] = field(default_factory=lambda: defaultdict(int))
    inbound_sequences: dict[str, int] = field(default_factory=dict)
    send_lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    heartbeat_task: asyncio.Task[None] | None = None
    last_inbound_s: float = 0.0
    protocol_version: int = PROTOCOL_VERSION
    pending_ping_nonce: str | None = None
    pending_ping_sent_s: float | None = None


def _close_reason(reason: str) -> str:
    """WebSocket close reasons are limited to 123 UTF-8 bytes."""

    encoded = reason.encode("utf-8")
    if len(encoded) <= 123:
        return reason
    return encoded[:123].decode("utf-8", errors="ignore")


class BridgeClient:
    """Maintain one authenticated full-duplex connection to the in-game host.

    All methods are asyncio methods and must be called on the client's owning
    event loop. Transient connection failures retry with bounded jittered
    backoff; authentication, version, path, size, and protocol-policy failures
    are terminal for the mission process.
    """

    def __init__(
        self,
        config: Config,
        *,
        on_envelope: EnvelopeHandler | None = None,
        on_connect: ConnectionHandler | None = None,
        on_disconnect: DisconnectHandler | None = None,
        on_latency: LatencyHandler | None = None,
    ) -> None:
        if not config.token:
            raise ServerError("a pairing token is required")
        self.config = config
        self.on_envelope = on_envelope
        self.on_connect = on_connect
        self.on_disconnect = on_disconnect
        self.on_latency = on_latency
        self._session: _PeerSession | None = None
        self._closed = asyncio.Event()
        self._stop_event = asyncio.Event()
        self._stop_lock = asyncio.Lock()
        self._stopping = False
        self._runner: asyncio.Task[None] | None = None

    @property
    def connection_info(self) -> ConnectionInfo:
        session = self._session
        return ConnectionInfo(False) if session is None else session.connection

    @property
    def connected(self) -> bool:
        session = self._session
        return session is not None and session.connection.connected

    async def start(self) -> None:
        if self._runner is not None and not self._runner.done():
            return
        self._stopping = False
        self._closed.clear()
        self._stop_event.clear()
        self._runner = asyncio.create_task(self._run(), name="kittenio-client")

    async def serve_forever(self) -> None:
        await self.start()
        runner = self._runner
        if runner is not None:
            await runner

    async def stop(self) -> None:
        async with self._stop_lock:
            runner = self._runner
            if self._stopping and (runner is None or runner.done()):
                self._closed.set()
                return
            self._stopping = True
            self._stop_event.set()
            session = self._session
            if session is not None:
                try:
                    await session.peer.close(code=1001, reason="KittenIO client stopping")
                except Exception:
                    pass
            if session is not None and session.heartbeat_task is not None:
                session.heartbeat_task.cancel()
                await asyncio.gather(session.heartbeat_task, return_exceptions=True)
            if runner is not None and asyncio.current_task() is not runner:
                runner.cancel()
                await asyncio.gather(runner, return_exceptions=True)
            self._runner = None
            self._session = None
            self._closed.set()

    async def _run(self) -> None:
        delay_s = 0.25
        try:
            while not self._stopping:
                try:
                    await self._connect_once()
                    delay_s = 0.25
                except asyncio.CancelledError:
                    raise
                except TerminalConnectionError:
                    raise
                except Exception as exc:
                    if self._is_terminal_exception(exc):
                        raise TerminalConnectionError(str(exc)) from exc
                if self._stopping:
                    break
                wait_s = delay_s + random.uniform(0.0, delay_s * 0.2)
                try:
                    await asyncio.wait_for(self._stop_event.wait(), timeout=wait_s)
                except asyncio.TimeoutError:
                    pass
                delay_s = min(5.0, delay_s * 2.0)
        finally:
            self._closed.set()

    @staticmethod
    def _is_terminal_exception(exc: BaseException) -> bool:
        response = getattr(exc, "response", None)
        status = getattr(response, "status_code", None)
        if status in {300, 301, 302, 303, 307, 308, 401, 403, 404, 426}:
            return True
        received_close = getattr(exc, "rcvd", None)
        code = getattr(received_close, "code", None)
        if code is None:
            code = getattr(exc, "close_code", None)
        # 1013 is the host's authenticated one-peer backpressure response. It
        # means "Try Again Later", so reconnect with the normal bounded
        # backoff. Policy and size failures are deterministic and terminal.
        return code in {1008, 1009}

    async def _connect_once(self) -> None:
        try:
            from websockets.asyncio.client import connect
        except ImportError as exc:  # pragma: no cover - package metadata prevents this
            raise ServerError("websockets 15 is required; install the kittenio package") from exc

        headers = {
            "Authorization": f"Bearer {self.config.token}",
            "X-KittenIO-Protocol": str(PROTOCOL_VERSION),
        }

        class _LoopbackOnlyConnect(connect):
            def process_redirect(self, exc: Exception) -> Exception | str:
                # The bearer token and v1 traffic must never leave the exact
                # configured loopback endpoint, even when a local HTTP server
                # attempts a same- or cross-origin redirect.
                return exc

        async with _LoopbackOnlyConnect(
            self.config.endpoint,
            additional_headers=headers,
            proxy=None,
            max_size=MAX_MESSAGE_BYTES,
            compression=None,
            ping_interval=None,
            max_queue=16,
            open_timeout=5.0,
            close_timeout=2.0,
        ) as peer:
            await self._handle_peer(peer)

    async def send(
        self,
        message_type: str,
        payload: dict[str, Any] | None = None,
        *,
        request_id: str | None = None,
        expected_session_id: str | None = None,
    ) -> None:
        await self._send_expected(
            message_type,
            payload,
            request_id=request_id,
            expected_session_id=expected_session_id,
        )

    async def _send_expected(
        self,
        message_type: str,
        payload: dict[str, Any] | None = None,
        *,
        request_id: str | None = None,
        expected_session_id: str | None = None,
        expected_game_instance_id: str | None = None,
    ) -> None:
        session = self._session
        if (
            session is None
            or not session.connection.connected
            or (expected_session_id is not None and session.connection.session_id != expected_session_id)
            or (
                expected_game_instance_id is not None
                and session.connection.game_instance_id != expected_game_instance_id
            )
        ):
            raise NotConnectedError("the KSA mod is not connected")
        await self._send_on_session(
            session,
            message_type,
            payload,
            request_id=request_id,
            expected_session_id=expected_session_id,
            expected_game_instance_id=expected_game_instance_id,
        )

    async def _send_on_session(
        self,
        session: _PeerSession,
        message_type: str,
        payload: dict[str, Any] | None = None,
        *,
        request_id: str | None = None,
        expected_session_id: str | None = None,
        expected_game_instance_id: str | None = None,
    ) -> None:
        async with session.send_lock:
            info = session.connection
            if (
                self._session is not session
                or not info.connected
                or (expected_session_id is not None and info.session_id != expected_session_id)
                or (expected_game_instance_id is not None and info.game_instance_id != expected_game_instance_id)
            ):
                raise NotConnectedError("the KSA protocol session changed before send")
            sequence = session.outbound_sequences[message_type]
            session.outbound_sequences[message_type] += 1
            envelope = Envelope.create(
                message_type,
                game_instance_id=info.game_instance_id,
                session_id=info.session_id,
                seq=sequence,
                request_id=request_id,
                payload=payload,
                version=session.protocol_version,
            )
            await session.peer.send(encode_envelope(envelope))

    async def _handle_peer(self, peer: Any) -> None:
        if self._stopping:
            await peer.close(code=1001, reason="KittenIO client stopping")
            return
        session = _PeerSession(peer=peer)
        self._session = session
        reason = "connection closed"
        connected_notified = False
        try:
            raw_hello = await asyncio.wait_for(peer.recv(), timeout=_HELLO_TIMEOUT_S)
            if not isinstance(raw_hello, str):
                raise ProtocolError("binary WebSocket messages are forbidden")
            hello_envelope = decode_envelope(raw_hello)
            if hello_envelope.type != "hello":
                raise ProtocolError("first message must be hello")
            hello = _HelloPayload.from_payload(hello_envelope.payload)
            session_id = str(uuid4())
            session.outbound_sequences.clear()
            session.inbound_sequences = {"hello": hello_envelope.seq}
            session.last_inbound_s = time.monotonic()
            session.protocol_version = PROTOCOL_VERSION
            session.connection = ConnectionInfo(
                connected=True,
                game_instance_id=hello_envelope.game_instance_id,
                session_id=session_id,
                game_version=hello.game_version,
                mod_version=hello.mod_version,
                starmap_version=hello.starmap_version,
                protocol_version=PROTOCOL_VERSION,
            )
            await self._send_on_session(
                session,
                "welcome",
                {
                    "selected_version": PROTOCOL_VERSION,
                    "session_id": session_id,
                    "sdk_version": SDK_VERSION,
                    "telemetry_hz": int(self.config.telemetry_hz),
                },
            )
            await _invoke(self.on_connect, session.connection, hello.capabilities)
            connected_notified = True
            session.heartbeat_task = asyncio.create_task(self._heartbeat(session), name="kittenio-heartbeat")
            async for message in peer:
                if not isinstance(message, str):
                    raise ProtocolError("binary WebSocket messages are forbidden")
                envelope = decode_envelope(message)
                validated_payload = self._validate_session(session, envelope)
                session.last_inbound_s = time.monotonic()
                if envelope.type == "ping":
                    await self._send_on_session(
                        session,
                        "pong",
                        dict(envelope.payload),
                        request_id=envelope.request_id,
                    )
                elif envelope.type == "pong":
                    await self._handle_pong(session, envelope.payload["nonce"])
                else:
                    await _invoke(self.on_envelope, envelope, validated_payload)
            close_code = getattr(peer, "close_code", None)
            if close_code in {1008, 1009}:
                raise TerminalConnectionError(
                    getattr(peer, "close_reason", None) or f"game host closed with policy code {close_code}"
                )
        except (ProtocolError, ModelError) as exc:
            reason = str(exc)
            try:
                await peer.close(code=1008, reason=_close_reason(reason))
            except Exception:
                pass
            raise TerminalConnectionError(reason) from exc
        except asyncio.TimeoutError:
            reason = "hello timeout"
            try:
                await peer.close(code=1011, reason=reason)
            except Exception:
                pass
            raise ServerError(reason)
        except asyncio.CancelledError:
            reason = "client stopping"
            raise
        except Exception as exc:
            reason = f"connection error: {exc}"
            if self._is_terminal_exception(exc):
                raise TerminalConnectionError(str(exc)) from exc
            raise
        finally:
            old_info = session.connection
            session.connection = replace(old_info, connected=False)
            if self._session is session:
                self._session = None
            if session.heartbeat_task is not None:
                session.heartbeat_task.cancel()
                await asyncio.gather(session.heartbeat_task, return_exceptions=True)
                session.heartbeat_task = None
            try:
                if connected_notified:
                    await _invoke(self.on_disconnect, session.connection, reason)
            except Exception:
                # Disconnect cleanup must not retain the peer or prevent a
                # subsequent authenticated reconnection.
                pass

    def _validate_session(self, session: _PeerSession, envelope: Envelope) -> ValidatedPayload:
        if envelope.type not in _MOD_TO_SDK_TYPES:
            raise ProtocolError(f"{envelope.type} is not valid from the game peer after handshake")
        if envelope.game_instance_id != session.connection.game_instance_id:
            raise ProtocolError("game_instance_id changed during a connection")
        if envelope.session_id != session.connection.session_id:
            raise ProtocolError("stale or foreign session_id")
        # Validate direction and typed payload before touching sequence state so
        # malformed or policy-forbidden traffic cannot grow tracking maps.
        validated_payload = _validate_mod_payload(envelope)
        previous = session.inbound_sequences.get(envelope.type)
        if previous is not None and envelope.seq <= previous:
            raise ProtocolError(f"non-monotonic {envelope.type} sequence")
        session.inbound_sequences[envelope.type] = envelope.seq
        return validated_payload

    async def _heartbeat(self, session: _PeerSession) -> None:
        while self._session is session and session.connection.connected:
            await asyncio.sleep(2.0)
            if self._session is not session or not session.connection.connected:
                return
            if time.monotonic() - session.last_inbound_s > 10.0:
                await session.peer.close(code=1011, reason="application heartbeat timeout")
                return
            nonce = str(uuid4())
            session.pending_ping_nonce = nonce
            session.pending_ping_sent_s = time.monotonic()
            await self._send_on_session(session, "ping", {"nonce": nonce})

    async def _handle_pong(self, session: _PeerSession, nonce: str) -> None:
        if nonce != session.pending_ping_nonce or session.pending_ping_sent_s is None:
            return
        round_trip_s = max(0.0, time.monotonic() - session.pending_ping_sent_s)
        session.pending_ping_nonce = None
        session.pending_ping_sent_s = None
        await _invoke(self.on_latency, session.connection, round_trip_s)
