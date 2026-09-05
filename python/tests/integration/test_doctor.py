from __future__ import annotations

import asyncio
import json
from dataclasses import replace
from pathlib import Path

import kittenio.cli as cli
import kittenio.transport.client as client_module
import pytest
from kittenio.cli import main
from kittenio.transport.client import TerminalConnectionError
from kittenio.wire.common import ConnectionInfo
from kittenio.wire.models import FlightState
from kittenio.wire.protocol import decode_envelope

FIXTURE = Path(__file__).resolve().parents[3] / "protocol" / "fixtures" / "v1" / "03-telemetry.json"
TEST_TOKEN = "test-placeholder-token-" + "x" * 32


def _config(tmp_path: Path, token: str = TEST_TOKEN) -> Path:
    path = tmp_path / "kittenio.json"
    path.write_text(
        json.dumps(
            {
                "endpoint": "ws://127.0.0.1:65534/kittenio/v1",
                "pairing_token": token,
                "telemetry_hz": 20,
                "command_deadman_ms": 750,
                "request_timeout_s": 5.0,
            }
        ),
        encoding="utf-8",
    )
    return path


def _state() -> FlightState:
    return FlightState.from_payload(decode_envelope(FIXTURE.read_text(encoding="utf-8")).payload)


class FakeBridgeClient:
    mode = "valid"
    instances: list["FakeBridgeClient"] = []

    def __init__(self, config, *, on_connect=None, on_envelope=None, **_kwargs) -> None:
        self.config = config
        self.on_connect = on_connect
        self.on_envelope = on_envelope
        self.stopped = False
        self.sent: list[str] = []
        self._done = asyncio.Event()
        self.__class__.instances.append(self)

    async def serve_forever(self) -> None:
        if self.mode in {"wrong-token", "malformed", "retired"}:
            messages = {
                "wrong-token": "authentication rejected",
                "malformed": "first message must be hello",
                "retired": "game peer must advertise only protocol version 1",
            }
            raise TerminalConnectionError(messages[self.mode])
        if self.mode == "unreachable" or self.mode == "handshake-timeout":
            await self._done.wait()
            return
        info = ConnectionInfo(
            True,
            game_instance_id="11111111-1111-4111-8111-111111111111",
            session_id="22222222-2222-4222-8222-222222222222",
            game_version="2026.9.7.5402",
            mod_version="0.1.0",
            starmap_version="0.4.6+57846b2971f95f48eb90a3483acacce832dd8751",
            protocol_version=1,
        )
        if self.mode == "wrong-ksa":
            info = replace(info, game_version="2026.9.8.1")
        if self.mode == "wrong-starmap":
            info = replace(info, starmap_version="0.4.7")
        if self.mode == "wrong-mod":
            info = replace(info, mod_version="0.2.0")
        capabilities = _state().capabilities
        self.on_connect(info, capabilities)
        if self.mode != "telemetry-timeout":
            self.on_envelope(object(), _state())
        await self._done.wait()

    async def stop(self) -> None:
        self.stopped = True
        self._done.set()


@pytest.fixture(autouse=True)
def fake_client(monkeypatch: pytest.MonkeyPatch):
    FakeBridgeClient.mode = "valid"
    FakeBridgeClient.instances.clear()
    monkeypatch.setattr(client_module, "BridgeClient", FakeBridgeClient)
    monkeypatch.setattr(cli, "_DOCTOR_CONNECT_TIMEOUT_S", 0.02)
    monkeypatch.setattr(cli, "_DOCTOR_TELEMETRY_TIMEOUT_S", 0.02)


def test_doctor_requires_configuration_and_token(tmp_path: Path, capsys) -> None:
    missing = tmp_path / "missing.json"
    assert main(["doctor", "--config", str(missing)]) == 1
    assert "not paired" in capsys.readouterr().out
    empty = _config(tmp_path, token="")
    assert main(["doctor", "--config", str(empty)]) == 1
    assert "not paired" in capsys.readouterr().out


@pytest.mark.parametrize(
    ("mode", "message"),
    [
        ("unreachable", "handshake timed out"),
        ("handshake-timeout", "handshake timed out"),
        ("wrong-token", "authentication rejected"),
        ("malformed", "first message must be hello"),
        ("retired", "only protocol version 1"),
        ("telemetry-timeout", "telemetry frame timed out"),
        ("wrong-ksa", "KSA 2026.9.8.1 is unsupported"),
        ("wrong-starmap", "StarMap 0.4.7 is unsupported"),
        ("wrong-mod", "mod 0.2.0 is unsupported"),
    ],
)
def test_doctor_fails_closed_and_cleans_up(
    tmp_path: Path,
    capsys,
    mode: str,
    message: str,
) -> None:
    FakeBridgeClient.mode = mode
    assert main(["doctor", "--config", str(_config(tmp_path))]) == 1
    output = capsys.readouterr().out
    assert message in output
    assert TEST_TOKEN not in output
    assert FakeBridgeClient.instances[-1].stopped
    assert FakeBridgeClient.instances[-1].sent == []


def test_doctor_reports_valid_authenticated_telemetry_without_control(
    tmp_path: Path,
    capsys,
) -> None:
    assert main(["doctor", "--config", str(_config(tmp_path))]) == 0
    output = capsys.readouterr().out
    assert "authenticated protocol-v1 session" in output
    assert "KittenIO mod: 0.1.0" in output
    assert "KSA: 2026.9.7.5402" in output
    assert "StarMap: 0.4.6+57846b" in output
    assert "valid frame 2456" in output
    assert "thrust-vector control=available" in output
    assert TEST_TOKEN not in output
    assert FakeBridgeClient.instances[-1].stopped
    assert FakeBridgeClient.instances[-1].sent == []
