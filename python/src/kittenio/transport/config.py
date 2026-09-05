"""Persistent, security-sensitive configuration for the local bridge."""

from __future__ import annotations

import json
import os
import secrets
import tempfile
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

DEFAULT_URL = "ws://127.0.0.1:8765/kittenio/v1"
CONFIG_ENV = "KITTENIO_CONFIG"


class ConfigError(ValueError):
    """Raised when a KittenIO configuration is invalid."""


def default_config_path() -> Path:
    override = os.environ.get(CONFIG_ENV)
    if override:
        return Path(override).expanduser().resolve()
    if os.name == "nt":
        base = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    else:
        base = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
    return base / "KittenIO" / "kittenio.json"


def _validate_url(url: str) -> None:
    parsed = urlparse(url)
    if parsed.scheme != "ws" or parsed.hostname != "127.0.0.1":
        raise ConfigError("v1 endpoint must use the literal loopback host 127.0.0.1 over ws://")
    if parsed.path != "/kittenio/v1":
        raise ConfigError("endpoint path must be /kittenio/v1")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ConfigError("endpoint must not contain credentials, query, or fragment")


@dataclass(frozen=True, slots=True)
class Config:
    endpoint: str = DEFAULT_URL
    pairing_token: str = ""
    telemetry_hz: int = 20
    command_deadman_ms: int = 750
    request_timeout_s: float = 5.0

    def __post_init__(self) -> None:
        _validate_url(self.endpoint)
        if self.pairing_token and len(self.pairing_token) < 43:
            raise ConfigError("pairing token must contain at least 256 bits of encoded token material")
        if isinstance(self.telemetry_hz, bool) or not isinstance(self.telemetry_hz, int):
            raise ConfigError("telemetry_hz must be an integer")
        if not (1 <= self.telemetry_hz <= 60):
            raise ConfigError("telemetry_hz must be between 1 and 60")
        if not (0.1 <= self.request_timeout_s <= 60.0):
            raise ConfigError("request_timeout_s must be between 0.1 and 60")
        if isinstance(self.command_deadman_ms, bool) or not isinstance(self.command_deadman_ms, int):
            raise ConfigError("command_deadman_ms must be an integer")
        if not (250 <= self.command_deadman_ms <= 750):
            raise ConfigError("command_deadman_ms must be between 250 and 750 in protocol v1")

    @property
    def url(self) -> str:
        """Python-friendly alias for the shared ``endpoint`` setting."""

        return self.endpoint

    @property
    def token(self) -> str:
        """Python-friendly alias for the shared ``pairing_token`` setting."""

        return self.pairing_token

    @property
    def bind_host(self) -> str:
        return urlparse(self.endpoint).hostname or "127.0.0.1"

    @property
    def bind_port(self) -> int:
        parsed = urlparse(self.endpoint)
        return parsed.port if parsed.port is not None else 80

    @property
    def path(self) -> str:
        return urlparse(self.endpoint).path

    def paired(self) -> "Config":
        return replace(self, pairing_token=secrets.token_urlsafe(32))

    def to_dict(self) -> dict[str, Any]:
        return {
            "endpoint": self.endpoint,
            "pairing_token": self.pairing_token,
            "telemetry_hz": self.telemetry_hz,
            "command_deadman_ms": self.command_deadman_ms,
            "request_timeout_s": self.request_timeout_s,
        }

    @classmethod
    def from_dict(cls, value: object) -> "Config":
        if not isinstance(value, dict):
            raise ConfigError("configuration root must be an object")
        allowed = {
            "endpoint",
            "pairing_token",
            "telemetry_hz",
            "command_deadman_ms",
            # Accepted so existing configurations remain readable after the
            # in-game diagnostics overlay was removed.
            "show_diagnostics",
            "request_timeout_s",
            # Accepted solely to migrate pre-v1 development configurations.
            "url",
            "token",
        }
        unknown = set(value) - allowed
        if unknown:
            raise ConfigError(f"unknown configuration keys: {', '.join(sorted(unknown))}")
        endpoint = value.get("endpoint", value.get("url", DEFAULT_URL))
        pairing_token = value.get("pairing_token", value.get("token", ""))
        telemetry_hz = value.get("telemetry_hz", 20)
        command_deadman_ms = value.get("command_deadman_ms", 750)
        request_timeout_s = value.get("request_timeout_s", 5.0)
        if not isinstance(endpoint, str) or not isinstance(pairing_token, str):
            raise ConfigError("endpoint and pairing_token must be strings")
        if isinstance(telemetry_hz, bool) or not isinstance(telemetry_hz, int):
            raise ConfigError("telemetry_hz must be an integer")
        if isinstance(command_deadman_ms, bool) or not isinstance(command_deadman_ms, int):
            raise ConfigError("command_deadman_ms must be an integer")
        try:
            return cls(
                endpoint=endpoint,
                pairing_token=pairing_token,
                telemetry_hz=telemetry_hz,
                command_deadman_ms=command_deadman_ms,
                request_timeout_s=float(request_timeout_s),
            )
        except (TypeError, ValueError) as exc:
            raise ConfigError(str(exc)) from exc


def load_config(path: Path | None = None, *, require_token: bool = True) -> Config:
    target = path or default_config_path()
    try:
        config = Config.from_dict(json.loads(target.read_text(encoding="utf-8")))
    except FileNotFoundError:
        config = Config()
    except (OSError, json.JSONDecodeError) as exc:
        raise ConfigError(f"cannot read {target}: {exc}") from exc
    if require_token and not config.token:
        raise ConfigError(f'KittenIO is not paired; run `kittenio pair --config "{target}"`')
    return config


def save_config(config: Config, path: Path | None = None) -> Path:
    """Atomically save *config*, limiting permissions where the OS supports it."""

    target = path or default_config_path()
    target.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(prefix=".config-", suffix=".tmp", dir=target.parent)
    temporary = Path(temporary_name)
    try:
        if os.name != "nt":
            os.fchmod(fd, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(config.to_dict(), stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, target)
        if os.name != "nt":
            target.chmod(0o600)
    finally:
        temporary.unlink(missing_ok=True)
    return target
