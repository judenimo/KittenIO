"""The ``kittenio`` command line interface."""

from __future__ import annotations

import argparse
import asyncio
import hashlib
import importlib
import importlib.util
import json
import sys
from importlib.metadata import PackageNotFoundError, version
from pathlib import Path
from types import ModuleType
from typing import Sequence

from .installer import InstallError, default_mods_directory, install_mod
from .runtime.program import KittenProgram, run
from .transport.config import DEFAULT_URL, Config, ConfigError, default_config_path, load_config, save_config
from .wire.common import ConnectionInfo
from .wire.models import CapabilitySet, FlightState

COMMANDS = frozenset({"run", "doctor", "pair", "install-mod"})
_LEGACY_CONFIG_KEYS = frozenset({"url", "token", "show_diagnostics"})
_DOCTOR_CONNECT_TIMEOUT_S = 6.0
_DOCTOR_TELEMETRY_TIMEOUT_S = 5.0
_SUPPORTED_MOD_VERSION = "0.1.0"
_SUPPORTED_GAME_VERSION = "2026.9.7.5402"
_SUPPORTED_STARMAP_VERSIONS = frozenset(
    {
        "0.4.6",
        "0.4.6+57846b2971f95f48eb90a3483acacce832dd8751",
    }
)


def _uses_legacy_config_keys(path: Path) -> bool:
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (FileNotFoundError, OSError, json.JSONDecodeError):
        return False
    return isinstance(raw, dict) and not _LEGACY_CONFIG_KEYS.isdisjoint(raw)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="kittenio",
        usage=(
            "kittenio [run] FILE_OR_MODULE [--program CLASS] [--config PATH]\n"
            "       kittenio {pair,doctor,install-mod} ..."
        ),
        description="Run KittenIO flight software from a Python file or module.",
        epilog="commands:\n  kittenio run mission.py --program Ascent\n  kittenio doctor",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    sub = parser.add_subparsers(dest="command", required=True)

    run_parser = sub.add_parser("run", help="run a KittenProgram from a file or module")
    run_parser.add_argument("target", help="mission.py or importable.module")
    run_parser.add_argument("--program", help="class name when the target defines multiple programs")
    run_parser.add_argument("--config", type=Path, help="configuration file")

    doctor = sub.add_parser("doctor", help="authenticate and validate one in-game telemetry session")
    doctor.add_argument("--config", type=Path, help="configuration file")

    pair = sub.add_parser("pair", help="create or rotate the local pairing token")
    pair.add_argument("--config", type=Path, help="configuration file")
    pair.add_argument("--url", default=DEFAULT_URL, help="loopback WebSocket URL")
    pair.add_argument("--force", action="store_true", help="rotate an existing token")

    install = sub.add_parser("install-mod", help="install a KittenIO mod release")
    install.add_argument("artifact", type=Path, help="KittenIO release zip or unpacked directory")
    install.add_argument("--mods-dir", type=Path, default=default_mods_directory())
    return parser


def _normalize_argv(argv: Sequence[str]) -> list[str]:
    normalized = list(argv)
    if normalized and not normalized[0].startswith("-") and normalized[0] not in COMMANDS:
        normalized.insert(0, "run")
    return normalized


def _reject_colon_selector(target: str) -> None:
    target_without_class, separator, possible_class = target.rpartition(":")
    is_windows_drive = len(target_without_class) == 1 and target_without_class.isalpha()
    if separator and possible_class.isidentifier() and not is_windows_drive:
        raise ValueError(
            f"the file.py:ClassName syntax was removed; use `{target_without_class} --program {possible_class}`"
        )


def _load_module(target: str) -> ModuleType:
    _reject_colon_selector(target)
    path = Path(target)
    if path.suffix == ".py" or path.is_file():
        path = path.expanduser().resolve()
        if not path.is_file():
            raise ImportError(f"flight program file does not exist: {path}")
        digest = hashlib.sha256(str(path).encode("utf-8")).hexdigest()[:16]
        module_name = f"kittenio_mission_{digest}"
        spec = importlib.util.spec_from_file_location(module_name, path)
        if spec is None or spec.loader is None:
            raise ImportError(f"cannot import {path}")
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        try:
            spec.loader.exec_module(module)
        except BaseException:
            sys.modules.pop(spec.name, None)
            raise
    else:
        module = importlib.import_module(target)
    return module


def _is_local_program(value: object, module: ModuleType) -> bool:
    return (
        isinstance(value, type)
        and value is not KittenProgram
        and issubclass(value, KittenProgram)
        and value.__module__ == module.__name__
    )


def load_program(target: str, program_name: str | None = None) -> type[KittenProgram]:
    """Load one locally defined flight-program class from *target*."""

    module = _load_module(target)
    if program_name is not None:
        value = getattr(module, program_name, None)
        if not _is_local_program(value, module):
            raise TypeError(f"{program_name!r} is not a locally defined KittenProgram subclass in {target}")
        return value

    candidates = sorted(
        {value for value in vars(module).values() if _is_local_program(value, module)},
        key=lambda candidate: candidate.__name__,
    )
    conventional = [candidate for candidate in candidates if candidate.__name__ == "Program"]
    if conventional:
        return conventional[0]
    if len(candidates) == 1:
        return candidates[0]
    if not candidates:
        raise TypeError(f"{target} does not define a KittenProgram subclass")
    names = ", ".join(candidate.__name__ for candidate in candidates)
    raise TypeError(
        f"{target} defines multiple KittenProgram subclasses ({names}); select one with --program ClassName"
    )


def _supported_game_version(value: str) -> bool:
    return value == _SUPPORTED_GAME_VERSION or value.startswith(_SUPPORTED_GAME_VERSION + "+")


def _version_problems(info: ConnectionInfo) -> list[str]:
    problems: list[str] = []
    if info.protocol_version != 1:
        problems.append(f"protocol {info.protocol_version} is unsupported; expected 1")
    if info.mod_version != _SUPPORTED_MOD_VERSION:
        problems.append(f"mod {info.mod_version} is unsupported; expected {_SUPPORTED_MOD_VERSION}")
    if not _supported_game_version(info.game_version):
        problems.append(f"KSA {info.game_version} is unsupported; expected {_SUPPORTED_GAME_VERSION}")
    if info.starmap_version not in _SUPPORTED_STARMAP_VERSIONS:
        problems.append(f"StarMap {info.starmap_version} is unsupported; expected 0.4.6")
    return problems


async def _wait_for_event_or_failure(
    event: asyncio.Event,
    runner: asyncio.Task[None],
    timeout_s: float,
    timeout_message: str,
) -> None:
    waiter = asyncio.create_task(event.wait())
    try:
        done, _ = await asyncio.wait(
            {waiter, runner},
            timeout=timeout_s,
            return_when=asyncio.FIRST_COMPLETED,
        )
        if not done:
            raise TimeoutError(timeout_message)
        if runner in done:
            await runner
            raise RuntimeError("connection closed before diagnostics completed")
    finally:
        waiter.cancel()
        await asyncio.gather(waiter, return_exceptions=True)


async def _doctor_session(config: Config) -> tuple[ConnectionInfo, CapabilitySet, FlightState]:
    from .transport.client import BridgeClient

    connected = asyncio.Event()
    telemetry_received = asyncio.Event()
    result: dict[str, object] = {}

    def on_connect(info: ConnectionInfo, capabilities: CapabilitySet) -> None:
        result["info"] = info
        result["capabilities"] = capabilities
        connected.set()

    def on_envelope(_envelope, payload) -> None:
        if isinstance(payload, FlightState) and "telemetry" not in result:
            result["telemetry"] = payload
            telemetry_received.set()

    client = BridgeClient(config, on_connect=on_connect, on_envelope=on_envelope)
    runner = asyncio.create_task(client.serve_forever(), name="kittenio-doctor")
    try:
        await _wait_for_event_or_failure(
            connected,
            runner,
            _DOCTOR_CONNECT_TIMEOUT_S,
            "authenticated protocol handshake timed out",
        )
        info = result["info"]
        assert isinstance(info, ConnectionInfo)
        incompatibilities = _version_problems(info)
        if incompatibilities:
            raise RuntimeError("; ".join(incompatibilities))
        await _wait_for_event_or_failure(
            telemetry_received,
            runner,
            _DOCTOR_TELEMETRY_TIMEOUT_S,
            "valid telemetry frame timed out",
        )
        capabilities = result["capabilities"]
        telemetry = result["telemetry"]
        assert isinstance(capabilities, CapabilitySet)
        assert isinstance(telemetry, FlightState)
        return info, capabilities, telemetry
    finally:
        await client.stop()
        if not runner.done():
            runner.cancel()
        await asyncio.gather(runner, return_exceptions=True)


def _availability(value: bool) -> str:
    return "available" if value else "unavailable"


def _doctor(path: Path | None) -> int:
    target = path or default_config_path()
    try:
        config = load_config(target, require_token=True)
    except ConfigError as exc:
        print(f"FAIL config: {exc}")
        return 1
    print(f"OK   Python: {sys.version.split()[0]}")
    try:
        print(f"OK   websockets: {version('websockets')}")
    except PackageNotFoundError:
        print("FAIL websockets is not installed")
        return 1
    print(f"OK   pairing token: configured in {target}")
    try:
        info, capabilities, telemetry = asyncio.run(_doctor_session(config))
    except Exception as exc:
        print(f"FAIL session: {exc}")
        return 1
    print(f"OK   endpoint: authenticated protocol-v1 session at {config.url}")
    print(f"OK   KittenIO mod: {info.mod_version}")
    print(f"OK   KSA: {info.game_version}")
    print(f"OK   StarMap: {info.starmap_version}")
    print(f"OK   telemetry: valid frame {telemetry.frame_number}")
    capability_summary = (
        ("thrust-vector control", capabilities.control.thrust_vector_target),
        ("dynamics", capabilities.dynamics.total_acceleration),
        ("propulsion", capabilities.propulsion.aggregate),
        ("rigid body", capabilities.rigid_body.inertia_tensor),
        ("atmosphere", capabilities.atmosphere.pressure),
        ("landing frame", capabilities.landing.local_frame),
        ("surface state", capabilities.surface.controllability),
        ("recording/replay", capabilities.recording_replay),
        ("time warp", capabilities.time_warp.control),
    )
    print("INFO capabilities: " + ", ".join(f"{name}={_availability(enabled)}" for name, enabled in capability_summary))
    if not capabilities.time_warp.control:
        print(f"INFO time warp: {capabilities.time_warp.unavailable_reason}")
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    raw_argv = sys.argv[1:] if argv is None else argv
    args = _parser().parse_args(_normalize_argv(raw_argv))
    try:
        if args.command == "run":
            run(load_program(args.target, args.program), config_path=args.config)
            return 0
        if args.command == "doctor":
            return _doctor(args.config)
        if args.command == "pair":
            target = args.config or default_config_path()
            existing = load_config(target, require_token=False)
            if existing.token and not args.force:
                config = existing
                if _uses_legacy_config_keys(target):
                    save_config(config, target)
                print(f"KittenIO is already paired at {target}; use --force to rotate the token")
            else:
                config = Config(
                    endpoint=args.url,
                    telemetry_hz=existing.telemetry_hz,
                    command_deadman_ms=existing.command_deadman_ms,
                    request_timeout_s=existing.request_timeout_s,
                ).paired()
                save_config(config, target)
                print(f"Paired KittenIO at {target}")
            return 0
        if args.command == "install-mod":
            destination, backup = install_mod(args.artifact, args.mods_dir)
            print(f"Installed KittenIO to {destination}")
            if backup:
                print(f"Backed up the previous installation to {backup}")
            return 0
    except KeyboardInterrupt:
        print("Stopped.", file=sys.stderr)
        return 130
    except (ConfigError, InstallError, ImportError, AttributeError, TypeError, ValueError) as exc:
        print(f"kittenio: {exc}", file=sys.stderr)
        return 2
    return 2
