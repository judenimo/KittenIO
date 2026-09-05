"""Git Bash + uv development orchestration for KittenIO."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import tomllib
import zipfile
from pathlib import Path, PurePosixPath
from uuid import UUID

REPO_ROOT = Path(__file__).resolve().parents[1]
PYTHON_ROOT = REPO_ROOT / "python"
PYPROJECT = REPO_ROOT / "pyproject.toml"
UV_LOCK = REPO_ROOT / "uv.lock"
ARTIFACT_ROOT = REPO_ROOT / "artifacts"
MOD_PROJECT = REPO_ROOT / "src" / "KittenIO.Mod" / "KittenIO.Mod.csproj"
MOD_MANIFEST = REPO_ROOT / "src" / "KittenIO.Mod" / "mod.toml"
SOLUTION = REPO_ROOT / "KittenIO.slnx"
MAX_MESSAGE_BYTES = 256 * 1024
HASH_CHUNK_BYTES = 1024 * 1024
RELEASE_VERSION = "0.1.0"
PINNED_KSA_VERSION = "2026.9.7.5402"
PINNED_KSA_SHA256 = "A8A5164204EED962DF9D9025E99F523D" "9A23EE2CE6E802B18216A9DA35506D2F"
PINNED_CORE_SETTINGS_SHA256 = "29B6DEFFBB8B7BA2EF0CB869D703309E" "6516AB86E962CA4AEEFEBB5F690551B5"
PINNED_STARMAP_VERSION = "0.4.6"
PINNED_STARMAP_COMMIT = "57846b2971f95f48eb90a3483acacce832dd8751"
PINNED_STARMAP_SHA256 = "E8E321F5EF60D1F1CE53598616148A77" "9F863879F7B9B303BE4512895F27C956"
REQUIRED_MOD_FILES = frozenset({
    "KittenIO/0Harmony.dll",
    "KittenIO/KittenIO.dll",
    "KittenIO/KittenIO.Protocol.dll",
    "KittenIO/KittenIO.deps.json",
    "KittenIO/mod.toml",
    "KittenIO/README.md",
    "KittenIO/LICENSE",
    "KittenIO/THIRD_PARTY_NOTICES.md",
    "KittenIO/licenses/Lib.Harmony-LICENSE.txt",
})
FORBIDDEN_BINARY_PATTERNS = (
    re.compile(r"^ksa\.dll$", re.IGNORECASE),
    re.compile(r"^starmap.*\.dll$", re.IGNORECASE),
    re.compile(r"^brutal.*\.dll$", re.IGNORECASE),
)
EXCLUDED_PARTS = {
    ".git",
    ".pytest_cache",
    ".uv-cache",
    ".venv",
    "__pycache__",
    "bin",
    "build",
    "dist",
    "obj",
}
TEST_PROJECTS = (
    REPO_ROOT / "tests" / "KittenIO.Protocol.Tests" / "KittenIO.Protocol.Tests.csproj",
    REPO_ROOT / "tests" / "KittenIO.Mod.Tests" / "KittenIO.Mod.Tests.csproj",
    REPO_ROOT / "tests" / "KittenIO.KsaContract.Tests" / "KittenIO.KsaContract.Tests.csproj",
)
FORBIDDEN_PUBLIC_PARTS = frozenset(
    {"artifacts", "build", "dist", "import", "starmap", "helloworld", "examples", "bin", "obj"}
)
FORBIDDEN_PUBLIC_SUFFIXES = frozenset(
    {".dll", ".exe", ".pdb", ".zip", ".whl", ".pfx", ".p12", ".pem", ".key"}
)
PRIVATE_NOTE_NAMES = frozenset({"private_kittenio_engineering.md", "private-notes.md", "release-private.md"})


class DevError(RuntimeError):
    """A developer command could not complete safely."""


class DevelopmentInputs:
    __slots__ = ("ksa_directory", "ksa_reference", "starmap_api")

    def __init__(self, ksa_directory: Path, ksa_reference: Path, starmap_api: Path) -> None:
        self.ksa_directory = ksa_directory
        self.ksa_reference = ksa_reference
        self.starmap_api = starmap_api


def run_checked(
    arguments: list[str],
    *,
    cwd: Path = REPO_ROOT,
    environment: dict[str, str] | None = None,
) -> None:
    display = " ".join(_display_argument(item) for item in arguments)
    print(f"+ {display}", flush=True)
    child_environment = None
    if environment is not None:
        child_environment = os.environ.copy()
        child_environment.update(environment)
    completed = subprocess.run(arguments, cwd=cwd, check=False, env=child_environment)
    if completed.returncode:
        raise DevError(f"command failed with exit code {completed.returncode}: {display}")


def _display_argument(value: str) -> str:
    return f'"{value}"' if any(character.isspace() for character in value) else value


def _git_bash_path(value: str) -> Path:
    match = re.fullmatch(r"/([A-Za-z])(?:/(.*))?", value)
    if os.name == "nt" and match:
        suffix = match.group(2) or ""
        return Path(f"{match.group(1).upper()}:/{suffix}")
    return Path(value)


def ksa_directory() -> Path:
    override = os.environ.get("KITTENIO_KSA_DIR")
    if override:
        return _git_bash_path(override).expanduser().resolve()
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise DevError("LOCALAPPDATA is unavailable; set KITTENIO_KSA_DIR in Git Bash")
    return (Path(local_app_data) / "Programs" / "Kitten Space Agency").resolve()


def _external_reference(
    environment_name: str,
    repository_relative: Path,
    description: str,
) -> Path:
    override = os.environ.get(environment_name)
    if override:
        resolved = _git_bash_path(override).expanduser().resolve()
        if not resolved.is_file():
            raise DevError(f"{environment_name} does not name an existing {description}: {resolved}")
        return resolved

    local = (REPO_ROOT / repository_relative).resolve()
    if local.is_file():
        return local

    raise DevError(
        f"required {description} is missing; set {environment_name} in Git Bash"
    )


def require_tool(name: str) -> str:
    executable = shutil.which(name)
    if executable is None:
        raise DevError(f"required tool is not on PATH: {name}")
    return executable


def _windows_product_version(path: Path) -> str:
    if os.name != "nt":
        raise DevError("pinned game and loader validation requires Windows")
    environment = os.environ.copy()
    environment["KITTENIO_VERSION_FILE"] = str(path)
    completed = subprocess.run(
        [
            "powershell.exe",
            "-NoProfile",
            "-Command",
            "(Get-Item -LiteralPath $env:KITTENIO_VERSION_FILE).VersionInfo.ProductVersion",
        ],
        check=False,
        capture_output=True,
        text=True,
        env=environment,
    )
    if completed.returncode:
        raise DevError(f"cannot read product version for {path}: {completed.stderr.strip()}")
    return completed.stdout.strip()


def _validate_pinned_file(
    path: Path,
    *,
    description: str,
    expected_sha256: str,
    expected_versions: frozenset[str] | None = None,
) -> None:
    actual_hash = _sha256_file(path).upper()
    if actual_hash != expected_sha256:
        raise DevError(f"{description} has SHA-256 {actual_hash}; expected {expected_sha256}")
    if expected_versions is not None:
        actual_version = _windows_product_version(path)
        if actual_version not in expected_versions:
            expected = " or ".join(sorted(expected_versions))
            raise DevError(f"{description} has product version {actual_version!r}; expected {expected}")


def validate_inputs() -> DevelopmentInputs:
    require_tool("uv")
    require_tool("dotnet")
    required_repo_files = (SOLUTION, MOD_PROJECT, MOD_MANIFEST, PYPROJECT, UV_LOCK)
    for path in required_repo_files:
        if not path.is_file():
            raise DevError(f"required repository file is missing: {path}")
    ksa = ksa_directory()
    ksa_reference = _external_reference(
        "KITTENIO_KSA_REFERENCE",
        Path("Import") / "KSA.dll",
        "KSA reference assembly",
    )
    starmap_api = _external_reference(
        "KITTENIO_STARMAP_API",
        Path("StarMap") / "StarMap.API" / "bin" / "Debug" / "net10.0" / "StarMap.API.dll",
        "StarMap API assembly",
    )
    game_references = (ksa_reference, starmap_api) + tuple(
        ksa / name
        for name in (
            "Brutal.ImGui.dll",
            "Brutal.ImGui.Abstractions.dll",
            "Brutal.Core.Numerics.dll",
            "Brutal.Core.Strings.dll",
        )
    )
    missing = [str(path) for path in game_references if not path.is_file()]
    if missing:
        raise DevError("required KSA build reference(s) are missing:\n  " + "\n  ".join(missing))
    _validate_pinned_file(
        ksa_reference,
        description="KSA reference assembly",
        expected_sha256=PINNED_KSA_SHA256,
    )
    # ProductVersion may append source metadata. The exact DLL hash remains the
    # authoritative identity, so admit that suffix only for the pinned base.
    ksa_product_version = _windows_product_version(ksa_reference)
    if not (
        ksa_product_version == PINNED_KSA_VERSION
        or ksa_product_version.startswith(PINNED_KSA_VERSION + "+")
    ):
        raise DevError(
            f"KSA reference assembly has product version {ksa_product_version!r}; expected {PINNED_KSA_VERSION}"
        )
    _validate_pinned_file(
        starmap_api,
        description="StarMap API assembly",
        expected_sha256=PINNED_STARMAP_SHA256,
        expected_versions=frozenset(
            {PINNED_STARMAP_VERSION, f"{PINNED_STARMAP_VERSION}+{PINNED_STARMAP_COMMIT}"}
        ),
    )
    core_settings = ksa / "Content" / "Core" / "mod.toml"
    if not core_settings.is_file():
        raise DevError(f"pinned KSA Core settings are missing: {core_settings}")
    _validate_pinned_file(
        core_settings,
        description="KSA Core settings",
        expected_sha256=PINNED_CORE_SETTINGS_SHA256,
    )
    return DevelopmentInputs(ksa, ksa_reference, starmap_api)


def _msbuild_properties(inputs: DevelopmentInputs) -> list[str]:
    return [
        f"-p:KsaDir={inputs.ksa_directory}",
        f"-p:KsaReferencePath={inputs.ksa_reference}",
        f"-p:StarMapApiPath={inputs.starmap_api}",
    ]


def _development_environment(inputs: DevelopmentInputs) -> dict[str, str]:
    return {
        "KITTENIO_KSA_DIR": str(inputs.ksa_directory),
        "KITTENIO_KSA_REFERENCE": str(inputs.ksa_reference),
        "KITTENIO_STARMAP_API": str(inputs.starmap_api),
    }


def _nonzero_uuid(value: object, context: str) -> UUID:
    if not isinstance(value, str):
        raise DevError(f"{context} must be a lowercase canonical UUID string")
    try:
        parsed = UUID(value)
    except (ValueError, AttributeError) as exc:
        raise DevError(f"{context} must be a lowercase canonical UUID string") from exc
    if parsed.int == 0:
        raise DevError(f"{context} must not be the empty UUID")
    if str(parsed) != value:
        raise DevError(f"{context} must be a lowercase canonical UUID string")
    return parsed


def _reject_json_constant(value: str) -> None:
    raise DevError(f"non-finite JSON number {value} is forbidden")


def _object_without_duplicates(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise DevError(f"duplicate JSON property: {key}")
        result[key] = value
    return result


def _validate_finite_json(value: object, context: str) -> None:
    if value is None or isinstance(value, (str, bool, int)):
        return
    if isinstance(value, float):
        if not math.isfinite(value):
            raise DevError(f"{context} contains a non-finite number")
        return
    if isinstance(value, dict):
        for key, item in value.items():
            _validate_finite_json(item, f"{context}.{key}")
        return
    if isinstance(value, list):
        for index, item in enumerate(value):
            _validate_finite_json(item, f"{context}[{index}]")
        return
    raise DevError(f"{context} contains unsupported JSON value {type(value).__name__}")


def _load_json_strict(raw: bytes, context: str) -> object:
    try:
        text = raw.decode("utf-8", errors="strict")
        value = json.loads(
            text,
            parse_constant=_reject_json_constant,
            object_pairs_hook=_object_without_duplicates,
        )
    except DevError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise DevError(f"{context} is not valid UTF-8 JSON: {exc}") from exc
    _validate_finite_json(value, context)
    return value


def validate_protocol_fixtures() -> int:
    schema_path = REPO_ROOT / "protocol" / "schema" / "v1" / "envelope.schema.json"
    fixture_root = REPO_ROOT / "protocol" / "fixtures" / "v1"
    try:
        schema = _load_json_strict(schema_path.read_bytes(), schema_path.name)
    except (OSError, DevError) as exc:
        raise DevError(f"cannot read protocol schema: {exc}") from exc
    if not isinstance(schema, dict):
        raise DevError("protocol schema must be a JSON object")
    properties = schema["properties"]
    allowed_fields = set(properties)
    required_fields = set(schema["required"])
    message_types = set(properties["type"]["enum"])
    protocol_version = properties["version"]["const"]
    paths = sorted(fixture_root.glob("*.json"))
    if not paths:
        raise DevError("no protocol fixtures were found")
    messages_by_type: dict[str, list[dict[str, object]]] = {}
    for path in paths:
        raw = path.read_bytes()
        if len(raw) > MAX_MESSAGE_BYTES:
            raise DevError(f"{path.name} exceeds the v1 message limit")
        message = _load_json_strict(raw, path.name)
        if not isinstance(message, dict):
            raise DevError(f"{path.name} is not a JSON object")
        missing = required_fields - set(message)
        unknown = set(message) - allowed_fields
        if missing or unknown:
            raise DevError(f"{path.name} envelope fields are invalid; missing={sorted(missing)}, unknown={sorted(unknown)}")
        if message["version"] != protocol_version or message["type"] not in message_types:
            raise DevError(f"{path.name} has an invalid protocol version or message type")
        _nonzero_uuid(message["game_instance_id"], f"{path.name}.game_instance_id")
        if not isinstance(message["seq"], int) or isinstance(message["seq"], bool) or message["seq"] < 0:
            raise DevError(f"{path.name}.seq must be a non-negative integer")
        sent_ms = message["sent_monotonic_ms"]
        if not isinstance(sent_ms, int) or isinstance(sent_ms, bool) or sent_ms < 0:
            raise DevError(f"{path.name}.sent_monotonic_ms must be a non-negative integer")
        if not isinstance(message["payload"], dict):
            raise DevError(f"{path.name}.payload must be an object")
        message_type = str(message["type"])
        if message_type == "hello":
            if message["session_id"] is not None:
                raise DevError("only hello may have a null session_id")
            if message["payload"].get("supported_versions") != [1]:
                raise DevError("hello must advertise only protocol version 1")
        else:
            _nonzero_uuid(message["session_id"], f"{path.name}.session_id")
        if message_type in {"request", "response", "lease.acquire", "lease.granted"}:
            if not isinstance(message.get("request_id"), str) or not message["request_id"].strip():
                raise DevError(f"{path.name} requires a non-empty request_id")
        payload = message["payload"]
        if message_type == "control":
            _nonzero_uuid(payload.get("lease_id"), f"{path.name}.payload.lease_id")
            control_seq = payload.get("control_seq")
            if not isinstance(control_seq, int) or isinstance(control_seq, bool) or control_seq < 0:
                raise DevError("control_seq must be a non-negative integer")
            expected = {"lease_id", "control_seq", "mode", "engine_on", "throttle", "thrust_vector_target"}
            if set(payload) != expected or payload.get("mode") != "thrust_vector_target":
                raise DevError("control must be one complete thrust_vector_target frame")
            target = payload.get("thrust_vector_target")
            direction = target.get("direction_cci") if isinstance(target, dict) else None
            if not isinstance(direction, dict) or set(direction) != {"x", "y", "z"}:
                raise DevError("control direction_cci must be a complete vector")
            components = tuple(direction[axis] for axis in ("x", "y", "z"))
            if any(isinstance(value, bool) or not isinstance(value, (int, float)) for value in components) or not math.isclose(sum(float(value) ** 2 for value in components), 1.0, abs_tol=1e-6):
                raise DevError("control direction_cci must be a finite unit vector")
            throttle = payload.get("throttle")
            if throttle is not None and (isinstance(throttle, bool) or not isinstance(throttle, (int, float)) or not math.isfinite(float(throttle)) or not 0 <= throttle <= 1):
                raise DevError(f"{path.name} throttle must be finite and within [0,1]")
        if message_type in {"lease.granted", "lease.release"}:
            _nonzero_uuid(payload.get("lease_id"), f"{path.name}.payload.lease_id")
        if message_type == "request":
            if payload.get("method") not in {
                "get_catalog",
                "activate_next_sequence",
                "set_active_vehicle",
                "set_target",
                "set_landing_site",
                "time_warp.set_rate",
                "time_warp.increase",
                "time_warp.decrease",
                "time_warp.cancel",
            }:
                raise DevError(f"{path.name} has an unknown request method")
            _nonzero_uuid(payload.get("command_id"), f"{path.name}.payload.command_id")
            if not isinstance(payload.get("params"), dict):
                raise DevError(f"{path.name}.payload.params must be an object")
            if payload.get("method") in {"activate_next_sequence", "set_target"}:
                _nonzero_uuid(
                    payload["params"].get("lease_id"),
                    f"{path.name}.payload.params.lease_id",
                )
            if payload.get("method") == "set_active_vehicle":
                params = payload["params"]
                vehicle_id = params.get("vehicle_id")
                if set(params) != {"vehicle_id"} or not isinstance(vehicle_id, str) or not vehicle_id.strip():
                    raise DevError(
                        f"{path.name}.payload.params must contain exactly one non-empty vehicle_id string"
                    )
        messages_by_type.setdefault(message_type, []).append(message)
    required_types = {"hello", "welcome", "telemetry", "control", "request", "response"}
    missing_types = required_types - set(messages_by_type)
    if missing_types:
        raise DevError(f"required fixture types are missing: {', '.join(sorted(missing_types))}")
    welcome_messages = messages_by_type["welcome"]
    if len(welcome_messages) != 1:
        raise DevError("fixtures require exactly one welcome message")
    welcome = welcome_messages[0]
    _nonzero_uuid(
        welcome["payload"].get("session_id"),
        "welcome.payload.session_id",
    )
    if welcome["session_id"] != welcome["payload"].get("session_id"):
        raise DevError("welcome envelope and payload session IDs differ")
    requests_by_id: dict[str, dict[str, object]] = {}
    responses_by_id: dict[str, dict[str, object]] = {}
    for message in messages_by_type["request"]:
        request_id = str(message["request_id"])
        if request_id in requests_by_id:
            raise DevError(f"duplicate request fixture ID: {request_id}")
        requests_by_id[request_id] = message
    for message in messages_by_type["response"]:
        request_id = str(message["request_id"])
        if request_id in responses_by_id:
            raise DevError(f"duplicate response fixture ID: {request_id}")
        responses_by_id[request_id] = message
    if not responses_by_id.keys() <= requests_by_id.keys():
        raise DevError("response fixture has no matching request")
    for request_id, request in requests_by_id.items():
        if request["payload"].get("method") != "set_active_vehicle":
            continue
        response = responses_by_id[request_id]
        expected_vehicle_id = request["payload"]["params"]["vehicle_id"]
        response_payload = response["payload"]
        if response_payload.get("ok") is not True or response_payload.get("result") != {
            "vehicle_id": expected_vehicle_id
        }:
            raise DevError(
                f"{request_id} set_active_vehicle response must echo the exact vehicle_id"
            )
    print(f"Validated {len(paths)} canonical KittenIO v1 fixtures.")
    return len(paths)


def check() -> None:
    validate_release_version()
    run_checked([require_tool("uv"), "lock", "--project", str(REPO_ROOT), "--check"])
    run_checked([sys.executable, "-m", "ruff", "check", str(REPO_ROOT)])
    validate_protocol_fixtures()
    print("KittenIO development checks passed.")


def build(inputs: DevelopmentInputs | None = None) -> None:
    inputs = inputs or validate_inputs()
    run_checked(
        [
            require_tool("dotnet"),
            "build",
            str(SOLUTION),
            "--configuration",
            "Release",
            "--maxcpucount:1",
            *_msbuild_properties(inputs),
        ]
    )
    print("KittenIO Release build completed.")


def test() -> None:
    inputs = validate_inputs()
    validate_protocol_fixtures()
    run_checked([sys.executable, "-m", "pytest"], cwd=REPO_ROOT)
    build(inputs)
    for project in TEST_PROJECTS:
        assembly = (
            project.parent
            / "bin"
            / "Release"
            / "net10.0"
            / f"{project.stem}.dll"
        )
        if not assembly.is_file():
            raise DevError(f"compiled test assembly is missing: {assembly}")
        run_checked(
            [require_tool("dotnet"), str(assembly)],
            environment=_development_environment(inputs),
        )
    print("All KittenIO Python, protocol, mod-safety, and KSA contract tests passed.")


def _assert_descendant(path: Path, parent: Path) -> Path:
    resolved = path.resolve()
    parent_resolved = parent.resolve()
    if resolved == parent_resolved:
        raise DevError(f"refusing to operate on root directory: {resolved}")
    try:
        resolved.relative_to(parent_resolved)
    except ValueError as exc:
        raise DevError(f"refusing to operate outside {parent_resolved}: {resolved}") from exc
    return resolved


def _remove_generated(path: Path) -> None:
    target = _assert_descendant(path, REPO_ROOT)
    if target.is_dir():
        shutil.rmtree(target)
    elif target.exists():
        target.unlink()


def clean() -> None:
    generated = [
        ARTIFACT_ROOT,
        REPO_ROOT / ".venv",
        REPO_ROOT / ".uv-cache",
        REPO_ROOT / ".pytest_cache",
        REPO_ROOT / "build",
        REPO_ROOT / "dist",
        REPO_ROOT / "__pycache__",
        PYTHON_ROOT / ".venv",
        PYTHON_ROOT / ".uv-cache",
        PYTHON_ROOT / ".pytest_cache",
        PYTHON_ROOT / "build",
        PYTHON_ROOT / "dist",
    ]
    for source_root in (REPO_ROOT / "src", REPO_ROOT / "tests"):
        for project in source_root.iterdir():
            if project.is_dir():
                generated.extend((project / "bin", project / "obj"))
    for path in generated:
        _remove_generated(path)
    for cache_root in (PYTHON_ROOT, REPO_ROOT / "scripts"):
        for cache in cache_root.rglob("__pycache__"):
            _remove_generated(cache)
    for cache_root in (REPO_ROOT, PYTHON_ROOT):
        for pattern in (".package-check*", ".pytest-temp-*", "pytest-cache-files-*", "*.egg-info"):
            for generated_path in cache_root.glob(pattern):
                _remove_generated(generated_path)
    print("Removed KittenIO-generated environments, caches, builds, and artifacts.")


def _extract_required(pattern: str, text: str, description: str) -> str:
    match = re.search(pattern, text)
    if match is None:
        raise DevError(f"cannot read {description}")
    return match.group(1)


def release_version_values() -> dict[str, str]:
    match = re.search(r'(?m)^\s*version\s*=\s*["\']([^"\']+)["\']\s*$', MOD_MANIFEST.read_text(encoding="utf-8"))
    if match is None:
        raise DevError("mod.toml does not contain a parseable version")
    directory_props = (REPO_ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    package_init = (PYTHON_ROOT / "src" / "kittenio" / "__init__.py").read_text(encoding="utf-8")
    changelog = (REPO_ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    return {
        "pyproject.toml": str(tomllib.loads(PYPROJECT.read_text(encoding="utf-8"))["project"]["version"]),
        "mod.toml": match.group(1),
        "Directory.Build.props": _extract_required(r"<VersionPrefix>([^<]+)</VersionPrefix>", directory_props, ".NET VersionPrefix"),
        "kittenio.__version__": _extract_required(r'(?m)^__version__\s*=\s*["\']([^"\']+)["\']$', package_init, "kittenio.__version__"),
        "CHANGELOG.md": _extract_required(r"(?m)^##\s+([0-9]+\.[0-9]+\.[0-9]+)(?:\s|$)", changelog, "latest changelog heading"),
    }


def _validate_release_version_values(values: dict[str, str]) -> str:
    mismatches = {name: value for name, value in values.items() if value != RELEASE_VERSION}
    if mismatches:
        details = ", ".join(f"{name}={value}" for name, value in mismatches.items())
        raise DevError(f"release version must be {RELEASE_VERSION} everywhere: {details}")
    return RELEASE_VERSION


def validate_release_version() -> str:
    return _validate_release_version_values(release_version_values())


def _forbidden_binary(name: str) -> bool:
    return any(pattern.match(name) for pattern in FORBIDDEN_BINARY_PATTERNS)


def _release_sources() -> list[tuple[Path, str]]:
    mod_output = REPO_ROOT / "src" / "KittenIO.Mod" / "bin" / "Release" / "net10.0"
    direct = [
        (mod_output / "0Harmony.dll", "KittenIO/0Harmony.dll"),
        (mod_output / "KittenIO.dll", "KittenIO/KittenIO.dll"),
        (mod_output / "KittenIO.Protocol.dll", "KittenIO/KittenIO.Protocol.dll"),
        (mod_output / "KittenIO.deps.json", "KittenIO/KittenIO.deps.json"),
        (MOD_MANIFEST, "KittenIO/mod.toml"),
        (REPO_ROOT / "README.md", "KittenIO/README.md"),
        (REPO_ROOT / "LICENSE", "KittenIO/LICENSE"),
        (REPO_ROOT / "THIRD_PARTY_NOTICES.md", "KittenIO/THIRD_PARTY_NOTICES.md"),
        (
            REPO_ROOT / "licenses" / "Lib.Harmony-LICENSE.txt",
            "KittenIO/licenses/Lib.Harmony-LICENSE.txt",
        ),
    ]
    for source, _ in direct:
        if not source.is_file():
            raise DevError(f"required release input is missing: {source}")
        if _forbidden_binary(source.name):
            raise DevError(f"release input contains a forbidden binary: {source.name}")
    entries = [entry for _, entry in direct]
    if len(entries) != len({entry.casefold() for entry in entries}):
        raise DevError("release inputs contain duplicate ZIP paths")
    return direct


def _create_mod_zip(destination: Path) -> None:
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for source, entry in _release_sources():
            archive.write(source, entry)


def _validate_archive_path(name: str, context: str) -> PurePosixPath:
    if "\\" in name:
        raise DevError(f"{context} contains a non-portable backslash path: {name}")
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts:
        raise DevError(f"{context} contains an unsafe path: {name}")
    if any(part in EXCLUDED_PARTS or part.startswith(".package-check") for part in path.parts):
        raise DevError(f"{context} contains an excluded build path: {name}")
    if _forbidden_binary(path.name):
        raise DevError(f"{context} contains a forbidden binary: {name}")
    return path


def validate_mod_zip(path: Path) -> None:
    with zipfile.ZipFile(path) as archive:
        names = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
        normalized = [_validate_archive_path(name, "mod ZIP").as_posix() for name in names]
        if len(normalized) != len({name.casefold() for name in normalized}):
            raise DevError("mod ZIP contains duplicate normalized entries")
        actual = set(names)
        if actual != REQUIRED_MOD_FILES:
            missing = REQUIRED_MOD_FILES - actual
            unexpected = actual - REQUIRED_MOD_FILES
            raise DevError(
                "mod ZIP does not match the release allowlist; "
                f"missing={sorted(missing)}, unexpected={sorted(unexpected)}"
            )
        try:
            dependencies = json.loads(
                archive.read("KittenIO/KittenIO.deps.json").decode("utf-8")
            )
            targets = dependencies["targets"]
            if not isinstance(targets, dict):
                raise TypeError("targets must be an object")
            runtime_assets: set[str] = set()
            for target in targets.values():
                if not isinstance(target, dict):
                    raise TypeError("each target must be an object")
                for library in target.values():
                    if not isinstance(library, dict):
                        raise TypeError("each target library must be an object")
                    for group_name in ("runtime", "runtimeTargets"):
                        group = library.get(group_name, {})
                        if not isinstance(group, dict):
                            raise TypeError(f"{group_name} must be an object")
                        for asset in group:
                            if not isinstance(asset, str) or not PurePosixPath(asset).name:
                                raise TypeError("runtime asset paths must be non-empty strings")
                            runtime_assets.add(PurePosixPath(asset).name)
            if not runtime_assets:
                raise TypeError("no runtime assets are declared")
        except (KeyError, TypeError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise DevError(f"mod ZIP has an invalid KittenIO.deps.json: {exc}") from exc
        unresolved = {
            asset for asset in runtime_assets if f"KittenIO/{asset}" not in names
        }
        if unresolved:
            raise DevError(
                "mod ZIP cannot resolve declared runtime assets: "
                f"{', '.join(sorted(unresolved))}"
            )


def validate_python_distribution(path: Path) -> None:
    names: list[str]
    if path.suffix == ".whl":
        with zipfile.ZipFile(path) as archive:
            names = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
    elif path.name.endswith(".tar.gz"):
        with tarfile.open(path, "r:gz") as archive:
            names = [member.name for member in archive.getmembers() if member.isfile()]
    else:
        raise DevError(f"unexpected Python distribution: {path.name}")
    if not names or not any("kittenio/__init__.py" in name.replace("\\", "/") for name in names):
        raise DevError(f"Python distribution does not contain the kittenio package: {path.name}")
    for name in names:
        normalized = _validate_archive_path(name.replace("\\", "/"), path.name)
        lowered_parts = {part.casefold() for part in normalized.parts}
        if lowered_parts & {"examples", "tests", "docs", "protocol", "fixtures"}:
            raise DevError(f"Python distribution contains public-release-excluded content: {name}")
        if path.suffix == ".whl":
            if not (
                normalized.parts[0] == "kittenio"
                or normalized.parts[0].startswith("kittenio-0.1.0.dist-info")
            ):
                raise DevError(f"wheel contains an unexpected top-level path: {name}")
        else:
            relative_parts = normalized.parts[1:]
            allowed = (
                relative_parts in {("LICENSE",), ("README.md",), ("pyproject.toml",), ("PKG-INFO",)}
                or relative_parts[:3] == ("python", "src", "kittenio")
            )
            if not allowed:
                raise DevError(f"sdist contains an unexpected path: {name}")


def _sanitize_sdist(path: Path) -> None:
    """Remove Hatch's mandatory VCS marker, then preserve only validated inputs."""

    temporary = path.with_name(f".{path.name}.tmp")
    try:
        with tarfile.open(path, "r:gz") as source, temporary.open("wb") as raw:
            with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
                with tarfile.open(fileobj=compressed, mode="w") as destination:
                    for member in source.getmembers():
                        normalized = PurePosixPath(member.name)
                        if normalized.name == ".gitignore" and len(normalized.parts) == 2:
                            continue
                        stream = source.extractfile(member) if member.isfile() else None
                        destination.addfile(member, stream)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _write_and_verify_checksums(files: list[Path]) -> Path:
    checksum_path = ARTIFACT_ROOT / "SHA256SUMS.txt"
    lines = []
    expected: dict[str, str] = {}
    for path in sorted(files, key=lambda item: item.relative_to(ARTIFACT_ROOT).as_posix()):
        relative = path.relative_to(ARTIFACT_ROOT).as_posix()
        digest = _sha256_file(path)
        expected[relative] = digest
        lines.append(f"{digest}  {relative}")
    checksum_path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    actual: dict[str, str] = {}
    for line in checksum_path.read_text(encoding="utf-8").splitlines():
        digest, relative = line.split("  ", 1)
        artifact = _assert_descendant(ARTIFACT_ROOT / relative, ARTIFACT_ROOT)
        actual[relative] = _sha256_file(artifact)
        if actual[relative] != digest:
            raise DevError(f"checksum verification failed: {relative}")
    if actual != expected:
        raise DevError("checksum manifest does not cover exactly the generated release files")
    return checksum_path


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(HASH_CHUNK_BYTES):
            digest.update(chunk)
    return digest.hexdigest()


def _venv_python(environment: Path) -> Path:
    return environment / ("Scripts/python.exe" if os.name == "nt" else "bin/python")


def _smoke_python_distribution(distribution: Path) -> None:
    with tempfile.TemporaryDirectory(prefix="kittenio-dist-smoke-") as temporary_name:
        environment = Path(temporary_name) / "venv"
        uv = require_tool("uv")
        run_checked([uv, "venv", "--python", sys.executable, str(environment)])
        python = _venv_python(environment)
        run_checked([uv, "pip", "install", "--python", str(python), str(distribution)])
        run_checked(
            [
                str(python),
                "-c",
                f"import kittenio; assert kittenio.__version__ == {RELEASE_VERSION!r}",
            ]
        )
        run_checked([str(python), "-m", "kittenio.cli", "--help"])


def _smoke_mod_install(mod_zip: Path) -> None:
    from kittenio.installer import install_mod

    with tempfile.TemporaryDirectory(prefix="kittenio-mod-smoke-") as temporary_name:
        root = Path(temporary_name)
        destination, backup = install_mod(mod_zip, root / "mods")
        if backup is not None:
            raise DevError("fresh temporary mod install unexpectedly created a backup")
        installed = {
            f"KittenIO/{item.relative_to(destination).as_posix()}"
            for item in destination.rglob("*")
            if item.is_file()
        }
        if installed != REQUIRED_MOD_FILES:
            raise DevError(
                "temporary StarMap layout does not match the release allowlist; "
                f"installed={sorted(installed)}"
            )
        manifest = root / "manifest.toml"
        if not manifest.is_file() or 'id = "KittenIO"' not in manifest.read_text(encoding="utf-8"):
            raise DevError("temporary StarMap layout did not enable KittenIO")


def _tracked_candidate_files() -> list[Path]:
    completed = subprocess.run(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
        cwd=REPO_ROOT,
        check=False,
        capture_output=True,
    )
    if completed.returncode:
        raise DevError("git ls-files failed during public audit")
    return [REPO_ROOT / item.decode("utf-8") for item in completed.stdout.split(b"\0") if item]


def public_audit() -> None:
    failures: list[str] = []
    windows_home = r"[A-Z]:\\" + "Users" + r"\\[^\\\s]+"
    mac_home = "/" + "Users" + r"/[^/\s]+"
    linux_home = "/" + "home" + r"/[^/\s]+"
    personal_path = re.compile(f"(?i)(?:{windows_home}|{mac_home}|{linux_home})")
    secret_markers = (
        re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
        re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
        re.compile(r"\bgh[pousr]_[A-Za-z0-9_]{30,}\b"),
        re.compile(r"\bgithub_pat_[A-Za-z0-9_]{20,}\b"),
    )
    credential_assignment = re.compile(
        r'''(?i)(?:api[_-]?key|password|secret|pairing[_-]?token|token)\s*[:=]\s*["']([^"']{20,})["']'''
    )
    deterministic_placeholders = {
        "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG",
        "test-placeholder-token-",
        "test-placeholder-token-" + "x" * 32,
        "legacy-token-" + "x" * 32,
    }
    product_branding = re.compile(r"(?i)KittenIO[- ]" + "No" + "va|pre" + r"-public")
    for path in _tracked_candidate_files():
        if not path.is_file():
            continue
        relative = path.relative_to(REPO_ROOT)
        lowered_parts = {part.casefold() for part in relative.parts}
        lowered_name = path.name.casefold()
        if lowered_parts & FORBIDDEN_PUBLIC_PARTS:
            failures.append(f"forbidden public path: {relative.as_posix()}")
        if lowered_name in PRIVATE_NOTE_NAMES or lowered_name in {"kittenio.json", ".env", ".env.local"}:
            failures.append(f"private/local file name: {relative.as_posix()}")
        compound_suffix = "".join(path.suffixes[-2:]).casefold()
        if path.suffix.casefold() in FORBIDDEN_PUBLIC_SUFFIXES or compound_suffix == ".tar.gz":
            failures.append(f"release artifact or binary is tracked: {relative.as_posix()}")
        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue
        match = personal_path.search(text)
        if match:
            failures.append(f"absolute personal path in {relative.as_posix()}: {match.group(0)}")
        if product_branding.search(text):
            failures.append(f"retired product branding in {relative.as_posix()}")
        for marker in secret_markers:
            if marker.search(text):
                failures.append(f"credential/private-key marker in {relative.as_posix()}")
                break
        for match in credential_assignment.finditer(text):
            if match.group(1) not in deterministic_placeholders:
                failures.append(f"credential-shaped literal in {relative.as_posix()}")
                break
    if failures:
        raise DevError("public audit failed:\n  " + "\n  ".join(sorted(set(failures))))
    print(f"Public audit passed for {len(_tracked_candidate_files())} candidate files.")


def package() -> None:
    inputs = validate_inputs()
    build(inputs)
    version = validate_release_version()
    _remove_generated(ARTIFACT_ROOT)
    python_artifacts = ARTIFACT_ROOT / "python"
    python_artifacts.mkdir(parents=True, exist_ok=True)
    run_checked(
        [require_tool("uv"), "build", "--project", str(REPO_ROOT), "--out-dir", str(python_artifacts)]
    )
    wheels = sorted(python_artifacts.glob("*.whl"))
    sdists = sorted(python_artifacts.glob("*.tar.gz"))
    if len(wheels) != 1 or len(sdists) != 1:
        raise DevError("uv build must produce exactly one wheel and one source distribution")
    for sdist in sdists:
        _sanitize_sdist(sdist)
    for distribution in wheels + sdists:
        validate_python_distribution(distribution)
        _smoke_python_distribution(distribution)
    mod_zip = ARTIFACT_ROOT / f"KittenIO-{version}.zip"
    _create_mod_zip(mod_zip)
    validate_mod_zip(mod_zip)
    _smoke_mod_install(mod_zip)
    checksum_path = _write_and_verify_checksums([mod_zip, *wheels, *sdists])
    print("Created and validated release artifacts:")
    for artifact in (mod_zip, *wheels, *sdists, checksum_path):
        print(f"  {artifact}")


def python_dist_check() -> None:
    validate_release_version()
    with tempfile.TemporaryDirectory(prefix="kittenio-python-build-") as temporary_name:
        destination = Path(temporary_name) / "dist"
        run_checked(
            [require_tool("uv"), "build", "--project", str(REPO_ROOT), "--out-dir", str(destination)]
        )
        distributions = sorted(destination.iterdir())
        wheels = [path for path in distributions if path.suffix == ".whl"]
        sdists = [path for path in distributions if path.name.endswith(".tar.gz")]
        if len(wheels) != 1 or len(sdists) != 1 or len(distributions) != 2:
            raise DevError("Python build must produce exactly one wheel and one source distribution")
        for sdist in sdists:
            _sanitize_sdist(sdist)
        for distribution in wheels + sdists:
            validate_python_distribution(distribution)
            _smoke_python_distribution(distribution)
    print("Python wheel and source distribution passed isolated installation checks.")


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description="KittenIO Git Bash + uv developer commands")
    result.add_argument(
        "command",
        choices=("check", "test", "build", "package", "python-dist-check", "public-audit", "clean"),
    )
    return result


def main(argv: list[str] | None = None) -> int:
    arguments = parser().parse_args(argv)
    try:
        {
            "check": check,
            "test": test,
            "build": build,
            "package": package,
            "python-dist-check": python_dist_check,
            "public-audit": public_audit,
            "clean": clean,
        }[arguments.command]()
    except KeyboardInterrupt:
        print("Stopped.", file=sys.stderr)
        return 130
    except (DevError, OSError, subprocess.SubprocessError, zipfile.BadZipFile, tarfile.TarError) as exc:
        print(f"kittenio dev: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
