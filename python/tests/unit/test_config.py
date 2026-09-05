from __future__ import annotations

import json
from pathlib import Path

import pytest
from kittenio.config import Config, ConfigError, load_config, save_config


def test_config_roundtrip_uses_shared_csharp_names(tmp_path: Path) -> None:
    target = tmp_path / "kittenio.json"
    config = Config(endpoint="ws://127.0.0.1:8765/kittenio/v1").paired()
    save_config(config, target)
    raw = json.loads(target.read_text())
    assert raw["endpoint"] == config.url
    assert raw["pairing_token"] == config.token
    assert raw["command_deadman_ms"] == 750
    assert "show_diagnostics" not in raw
    assert "url" not in raw and "token" not in raw
    assert load_config(target) == config


def test_legacy_development_keys_are_accepted_without_rewriting(tmp_path: Path) -> None:
    target = tmp_path / "legacy.json"
    original = json.dumps(
        {
            "url": "ws://127.0.0.1:8765/kittenio/v1",
            "token": "x" * 43,
            "show_diagnostics": True,
        },
        indent=2,
        sort_keys=True,
    ) + "\n"
    target.write_text(original, encoding="utf-8", newline="\n")

    config = load_config(target)

    assert config.endpoint.startswith("ws://127.0.0.1")
    assert config.pairing_token == "x" * 43
    assert target.read_text(encoding="utf-8") == original


@pytest.mark.parametrize(
    "url",
    [
        "ws://0.0.0.0:8765/kittenio/v1",
        "ws://localhost:8765/kittenio/v1",
        "ws://[::1]:8765/kittenio/v1",
        "wss://127.0.0.1:8765/kittenio/v1",
        "ws://127.0.0.1:8765/wrong",
    ],
)
def test_config_rejects_nonlocal_or_wrong_endpoint(url: str) -> None:
    with pytest.raises(ConfigError):
        Config(endpoint=url)


def test_load_requires_pairing_by_default(tmp_path: Path) -> None:
    with pytest.raises(ConfigError, match="not paired"):
        load_config(tmp_path / "missing.json")


def test_short_pairing_token_and_unsafe_deadman_are_rejected() -> None:
    with pytest.raises(ConfigError, match="256 bits"):
        Config(pairing_token="x" * 42)
    with pytest.raises(ConfigError, match="250 and 750"):
        Config(command_deadman_ms=751)
