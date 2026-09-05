from __future__ import annotations

import json

import pytest
from kittenio.protocol import (
    MAX_MESSAGE_BYTES,
    Envelope,
    ProtocolError,
    decode_envelope,
    encode_envelope,
)

GAME = "11111111-1111-4111-8111-111111111111"
SESSION = "22222222-2222-4222-8222-222222222222"
REQUEST = "33333333-3333-4333-8333-333333333333"


def test_envelope_round_trip_and_optional_request_id() -> None:
    original = Envelope.create(
        "request",
        game_instance_id=GAME,
        session_id=SESSION,
        seq=4,
        request_id=REQUEST,
        payload={"method": "get_catalog", "command_id": "command-a", "params": {}},
    )
    decoded = decode_envelope(encode_envelope(original))
    assert decoded == original
    assert decoded.payload["method"] == "get_catalog"


def test_null_session_is_only_valid_for_hello() -> None:
    hello = Envelope.create(
        "hello", game_instance_id=GAME, session_id=None, seq=0, payload={}
    )
    assert decode_envelope(encode_envelope(hello)).session_id is None
    with pytest.raises(ProtocolError, match="hello must use a null"):
        Envelope.create("hello", game_instance_id=GAME, session_id=SESSION, seq=0)
    with pytest.raises(ProtocolError, match="only in hello"):
        Envelope.create("telemetry", game_instance_id=GAME, session_id=None, seq=0)


def test_zero_uuid_is_rejected() -> None:
    with pytest.raises(ProtocolError, match="non-empty UUID"):
        Envelope.create(
            "hello",
            game_instance_id="00000000-0000-0000-0000-000000000000",
            session_id=None,
            seq=0,
        )


@pytest.mark.parametrize(
    "text, message",
    [
        ('{"version":1,"version":1}', "duplicate"),
        ("[]", "object"),
        ('{"version": NaN}', "non-finite"),
        ("not json", "invalid JSON"),
    ],
)
def test_decoder_rejects_malformed_json(text: str, message: str) -> None:
    with pytest.raises(ProtocolError, match=message):
        decode_envelope(text)


def test_decoder_rejects_missing_and_unknown_properties() -> None:
    value = {
        "version": 1,
        "type": "ping",
        "game_instance_id": GAME,
        "session_id": SESSION,
        "seq": 0,
        "sent_monotonic_ms": 1,
        "payload": {"nonce": "n"},
    }
    missing = dict(value)
    missing.pop("seq")
    with pytest.raises(ProtocolError, match="missing"):
        decode_envelope(json.dumps(missing))
    value["surprise"] = True
    with pytest.raises(ProtocolError, match="unknown"):
        decode_envelope(json.dumps(value))


@pytest.mark.parametrize("version", [2, 3])
def test_decoder_rejects_retired_protocol_versions(version: int) -> None:
    value = {
        "version": version,
        "type": "ping",
        "game_instance_id": GAME,
        "session_id": SESSION,
        "seq": 0,
        "sent_monotonic_ms": 1,
        "payload": {"nonce": "n"},
    }
    with pytest.raises(ProtocolError, match="unsupported protocol version"):
        decode_envelope(json.dumps(value))


def test_size_limit_is_utf8_bytes_not_characters() -> None:
    oversized = "é" * (MAX_MESSAGE_BYTES // 2 + 1)
    with pytest.raises(ProtocolError, match="256 KiB"):
        decode_envelope(oversized)


def test_payload_is_immutable_and_nonfinite_is_rejected() -> None:
    envelope = Envelope.create(
        "ping", game_instance_id=GAME, session_id=SESSION, seq=0, payload={"nonce": "x"}
    )
    with pytest.raises(TypeError):
        envelope.payload["nonce"] = "y"  # type: ignore[index]
    with pytest.raises(ProtocolError, match="non-finite"):
        Envelope.create(
            "ping", game_instance_id=GAME, session_id=SESSION, seq=0, payload={"value": float("inf")}
        )
