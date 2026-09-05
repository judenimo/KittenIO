from __future__ import annotations

# Keep the repository-level schema and cross-fixture checks importable without
# publishing the developer orchestrator as part of the SDK.
import importlib.util
import io
import json
import shutil
import tarfile
import zipfile
from pathlib import Path

import pytest
from kittenio.models import FlightState
from kittenio.protocol import decode_envelope, encode_envelope

FIXTURES = Path(__file__).resolve().parents[3] / "protocol" / "fixtures" / "v1"
DEV_SCRIPT = Path(__file__).resolve().parents[3] / "scripts" / "dev.py"
pytestmark = pytest.mark.skipif(
    not FIXTURES.is_dir(), reason="shared cross-language fixtures are available only in the monorepo"
)


def _load_dev_script():
    spec = importlib.util.spec_from_file_location("kittenio_dev", DEV_SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_every_shared_fixture_decodes_and_reencodes() -> None:
    paths = sorted(FIXTURES.glob("*.json"))
    assert len(paths) == 7
    decoded = [decode_envelope(path.read_text(encoding="utf-8")) for path in paths]
    assert all(item.version == 1 for item in decoded)
    assert [item.type for item in decoded[:4]] == ["hello", "welcome", "telemetry", "control"]
    for envelope in decoded:
        assert decode_envelope(encode_envelope(envelope)) == envelope


def test_shared_telemetry_fixture_matches_the_public_model() -> None:
    telemetry = decode_envelope((FIXTURES / "03-telemetry.json").read_text(encoding="utf-8"))
    state = FlightState.from_payload(telemetry.payload)
    assert state.vehicle is not None and state.vehicle.name == "Mittens I"
    assert state.propulsion is not None and state.propulsion.maximum_available_thrust_n == 150000.0
    assert state.atmosphere is not None and state.atmosphere.pressure_pa == 90000.0
    assert state.vehicle.position_orb_m is not None
    assert state.manual_control is not None


def test_time_warp_fixtures_are_correlated_and_canonical() -> None:
    request = decode_envelope((FIXTURES / "06-time-warp-set-rate-request.json").read_text(encoding="utf-8"))
    response = decode_envelope((FIXTURES / "07-time-warp-set-rate-response.json").read_text(encoding="utf-8"))
    assert request.request_id == response.request_id
    assert request.payload["method"] == "time_warp.set_rate"
    assert response.payload["result"]["confirmed_frame"] == 2457


def test_shared_fixtures_match_base_schema_and_cross_fixture_invariants() -> None:
    module = _load_dev_script()
    assert module.validate_protocol_fixtures() == 7


@pytest.mark.parametrize("corruption", ["duplicate", "nonfinite", "uuid"])
def test_fixture_checker_rejects_recursive_json_and_noncanonical_uuid_values(
    tmp_path: Path, corruption: str
) -> None:
    module = _load_dev_script()
    repository = tmp_path / "external fixture checkout with spaces"
    shutil.copytree(FIXTURES.parents[1], repository / "protocol")
    fixture = repository / "protocol" / "fixtures" / "v1" / "03-telemetry.json"
    text = fixture.read_text(encoding="utf-8")
    if corruption == "duplicate":
        text = text.replace(
            '"frame_number": 2456,',
            '"frame_number": 2456,\n    "frame_number": 2457,',
            1,
        )
    elif corruption == "nonfinite":
        text = text.replace('"x": 600100.0', '"x": 1e999', 1)
    else:
        canonical = "11111111-1111-4111-8111-111111111111"
        text = text.replace(canonical, "{" + canonical + "}", 1)
    fixture.write_text(text, encoding="utf-8")
    module.REPO_ROOT = repository
    with pytest.raises(module.DevError):
        module.validate_protocol_fixtures()


def test_release_checksums_stream_large_external_files(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    module = _load_dev_script()
    artifact_root = tmp_path / "external artifacts with spaces"
    artifact_root.mkdir()
    artifact = artifact_root / "large release.bin"
    artifact.write_bytes(b"z" * (1024 * 1024 + 23))
    module.ARTIFACT_ROOT = artifact_root

    def forbid_whole_file_reads(_path: Path) -> bytes:
        raise AssertionError("release checksums must stream file content")

    monkeypatch.setattr(Path, "read_bytes", forbid_whole_file_reads)
    checksum = module._write_and_verify_checksums([artifact])
    assert checksum.is_file()
    assert "large release.bin" in checksum.read_text(encoding="utf-8")


def test_mod_zip_resolves_every_declared_runtime_asset(tmp_path: Path) -> None:
    module = _load_dev_script()

    def write_archive(path: Path, runtime_assets: list[str]) -> None:
        dependencies = {
            "targets": {
                ".NETCoreApp,Version=v10.0": {
                    "KittenIO/0.1.0": {
                        "runtime": {asset: {} for asset in runtime_assets}
                    }
                }
            }
        }
        with zipfile.ZipFile(path, "w") as archive:
            for name in module.REQUIRED_MOD_FILES:
                content = (
                    json.dumps(dependencies).encode("utf-8")
                    if name == "KittenIO/KittenIO.deps.json"
                    else b"release"
                )
                archive.writestr(name, content)

    valid = tmp_path / "valid.zip"
    write_archive(
        valid,
        ["KittenIO.dll", "KittenIO.Protocol.dll", "lib/net10.0/0Harmony.dll"],
    )
    module.validate_mod_zip(valid)

    unresolved = tmp_path / "unresolved.zip"
    write_archive(unresolved, ["lib/net10.0/Missing.Runtime.dll"])
    with pytest.raises(module.DevError, match="cannot resolve declared runtime assets"):
        module.validate_mod_zip(unresolved)


def test_mod_zip_allowlist_is_exact_and_contains_runtime_licenses(tmp_path: Path) -> None:
    module = _load_dev_script()
    assert module.REQUIRED_MOD_FILES == {
        "KittenIO/0Harmony.dll",
        "KittenIO/KittenIO.dll",
        "KittenIO/KittenIO.Protocol.dll",
        "KittenIO/KittenIO.deps.json",
        "KittenIO/mod.toml",
        "KittenIO/README.md",
        "KittenIO/LICENSE",
        "KittenIO/THIRD_PARTY_NOTICES.md",
        "KittenIO/licenses/Lib.Harmony-LICENSE.txt",
    }
    artifact = tmp_path / "extra.zip"
    dependencies = {
        "targets": {
            ".NETCoreApp,Version=v10.0": {
                "KittenIO/0.1.0": {
                    "runtime": {
                        "KittenIO.dll": {},
                        "KittenIO.Protocol.dll": {},
                        "0Harmony.dll": {},
                    }
                }
            }
        }
    }
    with zipfile.ZipFile(artifact, "w") as archive:
        for name in module.REQUIRED_MOD_FILES:
            content = json.dumps(dependencies) if name.endswith(".deps.json") else "release"
            archive.writestr(name, content)
        archive.writestr("KittenIO/protocol/fixtures/v1/03-telemetry.json", "{}")
    with pytest.raises(module.DevError, match="unexpected"):
        module.validate_mod_zip(artifact)


@pytest.mark.parametrize(
    "excluded",
    [
        "python/examples/mission.py",
        "python/tests/test_package.py",
        "docs/engineering-guide.md",
        "protocol/fixtures/v1/03-telemetry.json",
        "KSA.dll",
        "StarMap.API.dll",
    ],
)
def test_sdist_rejects_non_runtime_content(tmp_path: Path, excluded: str) -> None:
    module = _load_dev_script()
    artifact = tmp_path / "kittenio-0.1.0.tar.gz"
    files = {
        "kittenio-0.1.0/LICENSE": b"license",
        "kittenio-0.1.0/README.md": b"readme",
        "kittenio-0.1.0/pyproject.toml": b"[project]",
        "kittenio-0.1.0/python/src/kittenio/__init__.py": b'__version__ = "0.1.0"',
        f"kittenio-0.1.0/{excluded}": b"excluded",
    }
    with tarfile.open(artifact, "w:gz") as archive:
        for name, content in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(content)
            archive.addfile(info, io.BytesIO(content))
    with pytest.raises(module.DevError):
        module.validate_python_distribution(artifact)


def test_sdist_sanitizer_removes_only_implicit_vcs_marker(tmp_path: Path) -> None:
    module = _load_dev_script()
    artifact = tmp_path / "kittenio-0.1.0.tar.gz"
    files = {
        "kittenio-0.1.0/.gitignore": b"artifacts/",
        "kittenio-0.1.0/LICENSE": b"license",
        "kittenio-0.1.0/README.md": b"readme",
        "kittenio-0.1.0/pyproject.toml": b"[project]",
        "kittenio-0.1.0/python/src/kittenio/__init__.py": b'__version__ = "0.1.0"',
    }
    with tarfile.open(artifact, "w:gz") as archive:
        for name, content in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(content)
            archive.addfile(info, io.BytesIO(content))
    module._sanitize_sdist(artifact)
    with tarfile.open(artifact, "r:gz") as archive:
        names = {member.name for member in archive.getmembers() if member.isfile()}
    assert "kittenio-0.1.0/.gitignore" not in names
    assert "kittenio-0.1.0/LICENSE" in names
    module.validate_python_distribution(artifact)


def test_external_reference_override_preserves_paths_with_spaces(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    module = _load_dev_script()
    reference = tmp_path / "external game files" / "KSA reference.dll"
    reference.parent.mkdir()
    reference.write_bytes(b"reference")
    monkeypatch.setenv("KITTENIO_TEST_REFERENCE", str(reference))
    resolved = module._external_reference(
        "KITTENIO_TEST_REFERENCE",
        Path("missing"),
        "test reference",
    )
    assert resolved == reference.resolve()
