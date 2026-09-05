from __future__ import annotations

import json
import zipfile
from pathlib import Path

import kittenio.cli as cli
import kittenio.installer as installer
import pytest
from kittenio.cli import load_program, main
from kittenio.installer import InstallError, install_mod


def test_pair_is_idempotent_and_uses_canonical_config(tmp_path: Path, capsys) -> None:
    config = tmp_path / "kittenio.json"
    assert main(["pair", "--config", str(config)]) == 0
    first = config.read_text()
    assert "pairing_token" in first
    token = json.loads(first)["pairing_token"]
    assert token not in capsys.readouterr().out
    assert main(["pair", "--config", str(config)]) == 0
    assert config.read_text() == first
    output = capsys.readouterr().out
    assert "already paired" in output
    assert token not in output


def test_pair_atomically_migrates_legacy_keys_without_rotating_token(
    tmp_path: Path, capsys
) -> None:
    target = tmp_path / "legacy kittenio.json"
    endpoint = "ws://127.0.0.1:9876/kittenio/v1"
    token = "legacy-token-" + "x" * 32
    target.write_text(
        json.dumps(
            {
                "url": endpoint,
                "token": token,
                "telemetry_hz": 17,
                "show_diagnostics": True,
            }
        ),
        encoding="utf-8",
    )

    assert main(["pair", "--config", str(target)]) == 0
    migrated = json.loads(target.read_text(encoding="utf-8"))
    assert migrated["endpoint"] == endpoint
    assert migrated["pairing_token"] == token
    assert migrated["telemetry_hz"] == 17
    assert "url" not in migrated
    assert "token" not in migrated
    assert "show_diagnostics" not in migrated
    output = capsys.readouterr().out
    assert "already paired" in output
    assert token not in output


def test_pair_has_no_token_printing_option(tmp_path: Path, capsys) -> None:
    with pytest.raises(SystemExit) as error:
        main(["pair", "--config", str(tmp_path / "kittenio.json"), "--show-token"])
    assert error.value.code == 2
    assert "unrecognized arguments" in capsys.readouterr().err


def _write_program(path: Path, body: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("from kittenio import KittenProgram\n" + body, encoding="utf-8")
    return path


def test_load_program_discovers_unique_subclass_from_path_with_spaces(tmp_path: Path) -> None:
    mission = _write_program(
        tmp_path / "mission folder" / "flight program.py",
        "class Mission(KittenProgram):\n    pass\n",
    )
    assert load_program(str(mission)).__name__ == "Mission"


def test_program_named_program_wins_over_other_local_subclasses(tmp_path: Path) -> None:
    mission = _write_program(
        tmp_path / "mission.py",
        "class Helper(KittenProgram):\n    pass\n"
        "class Program(KittenProgram):\n    pass\n",
    )
    assert load_program(str(mission)).__name__ == "Program"


def test_explicit_program_disambiguates_multiple_subclasses(tmp_path: Path) -> None:
    mission = _write_program(
        tmp_path / "mission.py",
        "class Ascent(KittenProgram):\n    pass\n"
        "class Descent(KittenProgram):\n    pass\n",
    )
    assert load_program(str(mission), "Descent").__name__ == "Descent"
    with pytest.raises(TypeError, match="multiple KittenProgram subclasses.*Ascent, Descent"):
        load_program(str(mission))


def test_imported_program_subclasses_are_not_discovered(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _write_program(
        tmp_path / "imported_program.py",
        "class ImportedProgram(KittenProgram):\n    pass\n",
    )
    mission = _write_program(
        tmp_path / "mission.py",
        "from imported_program import ImportedProgram\n"
        "class LocalProgram(KittenProgram):\n    pass\n",
    )
    monkeypatch.syspath_prepend(str(tmp_path))
    assert load_program(str(mission)).__name__ == "LocalProgram"


def test_importable_module_target_uses_the_same_discovery(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _write_program(
        tmp_path / "kittenio_test_mission_module.py",
        "class ModuleProgram(KittenProgram):\n    pass\n",
    )
    monkeypatch.syspath_prepend(str(tmp_path))
    assert load_program("kittenio_test_mission_module").__name__ == "ModuleProgram"


def test_missing_and_invalid_explicit_programs_fail_clearly(tmp_path: Path) -> None:
    empty = tmp_path / "empty.py"
    empty.write_text("VALUE = 1\n", encoding="utf-8")
    with pytest.raises(TypeError, match="does not define a KittenProgram subclass"):
        load_program(str(empty))

    mission = _write_program(
        tmp_path / "mission.py",
        "class Mission(KittenProgram):\n    pass\n",
    )
    with pytest.raises(TypeError, match="not a locally defined"):
        load_program(str(mission), "Missing")


def test_colon_class_selector_has_migration_error(tmp_path: Path) -> None:
    mission = _write_program(
        tmp_path / "mission.py",
        "class Mission(KittenProgram):\n    pass\n",
    )
    with pytest.raises(ValueError, match="syntax was removed.*--program Mission"):
        load_program(f"{mission}:Mission")


def test_cli_accepts_implicit_and_explicit_run_forms(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    mission = _write_program(
        tmp_path / "mission.py",
        "class Mission(KittenProgram):\n    pass\n",
    )
    calls: list[tuple[type, Path | None]] = []

    def fake_run(program, *, config_path=None):
        calls.append((program, config_path))

    monkeypatch.setattr(cli, "run", fake_run)
    assert main([str(mission)]) == 0
    assert main(["run", str(mission), "--program", "Mission"]) == 0
    assert [program.__name__ for program, _ in calls] == ["Mission", "Mission"]


def test_top_level_help_advertises_filename_first_usage(capsys) -> None:
    with pytest.raises(SystemExit) as error:
        main(["--help"])
    assert error.value.code == 0
    output = capsys.readouterr().out
    assert "kittenio [run] FILE_OR_MODULE" in output
    assert "kittenio run mission.py" in output


def test_nonexistent_python_file_is_not_treated_as_a_module(tmp_path: Path) -> None:
    mission = tmp_path / "missing.py"
    with pytest.raises(ImportError, match="flight program file does not exist"):
        load_program(str(mission))


def test_programmatic_named_subclass_remains_supported(tmp_path: Path) -> None:
    mission = tmp_path / "mission.py"
    mission.write_text(
        "from kittenio import KittenProgram\nclass Mission(KittenProgram):\n    pass\n",
        encoding="utf-8",
    )
    assert load_program(str(mission), "Mission").__name__ == "Mission"


def test_installer_accepts_zip_and_backs_up_previous_install(tmp_path: Path) -> None:
    artifact = tmp_path / "KittenIO.zip"
    with zipfile.ZipFile(artifact, "w") as archive:
        archive.writestr("KittenIO/mod.toml", 'name = "KittenIO"\n')
        archive.writestr("KittenIO/0Harmony.dll", b"harmony")
        archive.writestr("KittenIO/KittenIO.dll", b"first")
        archive.writestr("KittenIO/KittenIO.Protocol.dll", b"protocol")
        archive.writestr("KittenIO/KittenIO.deps.json", b"{}")
    mods = tmp_path / "mods"
    destination, backup = install_mod(artifact, mods)
    assert backup is None
    assert (destination / "KittenIO.dll").read_bytes() == b"first"
    manifest = tmp_path / "manifest.toml"
    assert 'id = "KittenIO"' in manifest.read_text()
    assert "enabled = true" in manifest.read_text()
    with zipfile.ZipFile(artifact, "w") as archive:
        archive.writestr("KittenIO/mod.toml", 'name = "KittenIO"\n')
        archive.writestr("KittenIO/0Harmony.dll", b"harmony")
        archive.writestr("KittenIO/KittenIO.dll", b"second")
        archive.writestr("KittenIO/KittenIO.Protocol.dll", b"protocol")
        archive.writestr("KittenIO/KittenIO.deps.json", b"{}")
    destination, backup = install_mod(artifact, mods)
    assert backup is not None
    assert (backup / "KittenIO.dll").read_bytes() == b"first"
    assert (destination / "KittenIO.dll").read_bytes() == b"second"


def test_installer_is_idempotent_and_preserves_other_manifest_blocks(tmp_path: Path) -> None:
    source = tmp_path / "source"
    source.mkdir()
    (source / "mod.toml").write_text('name = "KittenIO"\n')
    (source / "0Harmony.dll").write_bytes(b"harmony")
    (source / "KittenIO.dll").write_bytes(b"mod")
    (source / "KittenIO.Protocol.dll").write_bytes(b"protocol")
    (source / "KittenIO.deps.json").write_text("{}")
    mods = tmp_path / "mods"
    manifest = tmp_path / "manifest.toml"
    manifest.write_text('[[mods]]\nid = "Other"\nenabled = false\n\n[[mods]]\nid = "KittenIO"\nenabled = false\n')
    destination, backup = install_mod(source, mods)
    assert backup is None
    text = manifest.read_text()
    assert 'id = "Other"\nenabled = false' in text
    assert text.count('id = "KittenIO"') == 1
    assert 'id = "KittenIO"\nenabled = true' in text
    destination2, backup2 = install_mod(source, mods)
    assert destination2 == destination
    assert backup2 is None


def test_installer_rejects_game_and_loader_binaries(tmp_path: Path) -> None:
    source = tmp_path / "source"
    source.mkdir()
    for name in (
        "mod.toml",
        "0Harmony.dll",
        "KittenIO.dll",
        "KittenIO.Protocol.dll",
        "KittenIO.deps.json",
        "KSA.dll",
    ):
        (source / name).write_bytes(b"x")
    try:
        install_mod(source, tmp_path / "mods")
    except Exception as exc:
        assert "forbidden" in str(exc)
    else:
        raise AssertionError("bundled KSA.dll was accepted")


def test_installer_requires_harmony_runtime_dependency(tmp_path: Path) -> None:
    artifact = tmp_path / "missing-harmony.zip"
    with zipfile.ZipFile(artifact, "w") as archive:
        archive.writestr("KittenIO/mod.toml", 'name = "KittenIO"\n')
        archive.writestr("KittenIO/KittenIO.dll", b"mod")
        archive.writestr("KittenIO/KittenIO.Protocol.dll", b"protocol")
        archive.writestr("KittenIO/KittenIO.deps.json", b"{}")

    with pytest.raises(InstallError, match="0Harmony.dll"):
        install_mod(artifact, tmp_path / "mods")


def test_installer_rejects_zip_slip(tmp_path: Path) -> None:
    artifact = tmp_path / "bad.zip"
    with zipfile.ZipFile(artifact, "w") as archive:
        archive.writestr("../escape/mod.toml", "bad")
    try:
        install_mod(artifact, tmp_path / "mods")
    except Exception as exc:
        assert "unsafe archive path" in str(exc)
    else:
        raise AssertionError("zip slip was accepted")


def test_installer_normalizes_windows_zip_separators(tmp_path: Path) -> None:
    artifact = tmp_path / "windows.zip"
    with zipfile.ZipFile(artifact, "w") as archive:
        archive.writestr(r"KittenIO\mod.toml", 'name = "KittenIO"\n')
        archive.writestr(r"KittenIO\0Harmony.dll", b"harmony")
        archive.writestr(r"KittenIO\KittenIO.dll", b"mod")
        archive.writestr(r"KittenIO\KittenIO.Protocol.dll", b"protocol")
        archive.writestr(r"KittenIO\KittenIO.deps.json", b"{}")
    destination, _ = install_mod(artifact, tmp_path / "mods")
    assert (destination / "mod.toml").is_file()


def test_installer_restores_previous_mod_when_atomic_swap_fails(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    source = tmp_path / "source"
    source.mkdir()
    (source / "mod.toml").write_text('name = "KittenIO"\n')
    (source / "0Harmony.dll").write_bytes(b"harmony")
    (source / "KittenIO.dll").write_bytes(b"new")
    (source / "KittenIO.Protocol.dll").write_bytes(b"protocol")
    (source / "KittenIO.deps.json").write_text("{}")
    mods = tmp_path / "mods"
    destination = mods / "KittenIO"
    destination.mkdir(parents=True)
    (destination / "mod.toml").write_text('name = "KittenIO"\n')
    (destination / "0Harmony.dll").write_bytes(b"harmony")
    (destination / "KittenIO.dll").write_bytes(b"old")
    (destination / "KittenIO.Protocol.dll").write_bytes(b"protocol")
    (destination / "KittenIO.deps.json").write_text("{}")

    original_replace = installer.os.replace
    calls = 0

    def fail_new_target(source_path, destination_path):
        nonlocal calls
        calls += 1
        if calls == 2:
            raise OSError("injected atomic swap failure")
        return original_replace(source_path, destination_path)

    monkeypatch.setattr(installer.os, "replace", fail_new_target)
    with pytest.raises(OSError, match="injected"):
        install_mod(source, mods)

    assert (destination / "KittenIO.dll").read_bytes() == b"old"
    assert not list(mods.glob(".KittenIO.install-*"))


@pytest.mark.parametrize(
    "duplicate_name",
    ["KittenIO/./mod.toml", "kittenio/MOD.toml"],
)
def test_installer_rejects_duplicate_normalized_and_windows_case_destinations(
    tmp_path: Path, duplicate_name: str
) -> None:
    artifact = tmp_path / "duplicate.zip"
    with zipfile.ZipFile(artifact, "w") as archive:
        archive.writestr("KittenIO/mod.toml", 'name = "KittenIO"\n')
        archive.writestr(duplicate_name, 'name = "collision"\n')
        archive.writestr("KittenIO/KittenIO.dll", b"mod")
        archive.writestr("KittenIO/KittenIO.Protocol.dll", b"protocol")
        archive.writestr("KittenIO/KittenIO.deps.json", b"{}")
    with pytest.raises(InstallError, match="duplicate normalized destination"):
        install_mod(artifact, tmp_path / "mods")


def test_installer_rejects_excessive_zip_expansion_before_extraction(
    tmp_path: Path,
) -> None:
    artifact = tmp_path / "expansion-bomb.zip"
    highly_compressible = b"0" * (
        (installer._MAX_ARCHIVE_COMPRESSION_RATIO + 10) * 1024
    )
    with zipfile.ZipFile(
        artifact, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9
    ) as archive:
        archive.writestr("KittenIO/mod.toml", 'name = "KittenIO"\n')
        archive.writestr("KittenIO/KittenIO.dll", highly_compressible)
        archive.writestr("KittenIO/KittenIO.Protocol.dll", b"protocol")
        archive.writestr("KittenIO/KittenIO.deps.json", b"{}")

    with pytest.raises(InstallError, match="expansion ratio"):
        install_mod(artifact, tmp_path / "mods")
    assert not (tmp_path / "mods").exists()


def test_installer_rejects_excessive_zip_entry_count(tmp_path: Path) -> None:
    artifact = tmp_path / "too-many-entries.zip"
    with zipfile.ZipFile(artifact, "w") as archive:
        for index in range(installer._MAX_ARCHIVE_ENTRIES + 1):
            archive.writestr(f"KittenIO/empty-{index}.txt", b"")

    with pytest.raises(InstallError, match="more than .* entries"):
        install_mod(artifact, tmp_path / "mods")
    assert not (tmp_path / "mods").exists()


def test_installer_hashes_large_files_streamingly_and_remains_idempotent(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    source = tmp_path / "release source with spaces"
    source.mkdir()
    (source / "mod.toml").write_text('name = "KittenIO"\n')
    (source / "0Harmony.dll").write_bytes(b"harmony")
    (source / "KittenIO.dll").write_bytes(b"x" * (1024 * 1024 + 17))
    (source / "KittenIO.Protocol.dll").write_bytes(b"protocol")
    (source / "KittenIO.deps.json").write_text("{}")

    def forbid_whole_file_reads(_path: Path) -> bytes:
        raise AssertionError("fingerprinting must stream rather than call Path.read_bytes")

    monkeypatch.setattr(Path, "read_bytes", forbid_whole_file_reads)
    mods = tmp_path / "external user data with spaces" / "mods"
    first, first_backup = install_mod(source, mods)
    second, second_backup = install_mod(source, mods)
    assert first == second == mods / "KittenIO"
    assert first_backup is None and second_backup is None
    assert (first / "KittenIO.dll").stat().st_size == 1024 * 1024 + 17


def test_installer_rejects_directory_artifact_overlapping_destination(
    tmp_path: Path,
) -> None:
    source = tmp_path / "release source"
    source.mkdir()
    (source / "mod.toml").write_text('name = "KittenIO"\n')
    (source / "0Harmony.dll").write_bytes(b"harmony")
    (source / "KittenIO.dll").write_bytes(b"mod")
    (source / "KittenIO.Protocol.dll").write_bytes(b"protocol")
    (source / "KittenIO.deps.json").write_text("{}")
    nested_mods = source / "user data" / "mods"

    with pytest.raises(InstallError, match="must not overlap"):
        install_mod(source, nested_mods)

    assert not nested_mods.exists()


def test_installer_rejects_linked_descendant_into_mods_tree(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    source = tmp_path / "release source"
    source.mkdir()
    (source / "mod.toml").write_text('name = "KittenIO"\n')
    (source / "0Harmony.dll").write_bytes(b"harmony")
    (source / "KittenIO.dll").write_bytes(b"mod")
    (source / "KittenIO.Protocol.dll").write_bytes(b"protocol")
    (source / "KittenIO.deps.json").write_text("{}")
    mods = tmp_path / "user data" / "mods"
    mods.mkdir(parents=True)
    linked_mods = source / "linked-mods"
    try:
        linked_mods.symlink_to(mods, target_is_directory=True)
    except (NotImplementedError, OSError):
        # Windows may require an unavailable symlink privilege. Exercise the
        # same rejection path by simulating the reparse-bit result for this
        # otherwise ordinary descendant.
        linked_mods.mkdir()
        original_probe = installer._is_link_or_reparse
        monkeypatch.setattr(
            installer,
            "_is_link_or_reparse",
            lambda path: path == linked_mods or original_probe(path),
        )

    with pytest.raises(InstallError, match="linked or reparse path"):
        install_mod(source, mods)

    assert not (mods / "KittenIO").exists()
    assert not list(mods.glob(".KittenIO.install-*"))


def test_zip_install_supports_external_artifact_and_destination_paths_with_spaces(
    tmp_path: Path,
) -> None:
    artifact = tmp_path / "external releases" / "KittenIO release.zip"
    artifact.parent.mkdir()
    with zipfile.ZipFile(artifact, "w") as archive:
        archive.writestr("KittenIO/mod.toml", 'name = "KittenIO"\n')
        archive.writestr("KittenIO/0Harmony.dll", b"harmony")
        archive.writestr("KittenIO/KittenIO.dll", b"mod")
        archive.writestr("KittenIO/KittenIO.Protocol.dll", b"protocol")
        archive.writestr("KittenIO/KittenIO.deps.json", b"{}")
    mods = tmp_path / "redirected documents" / "My Games" / "mods with spaces"
    destination, _ = install_mod(artifact, mods)
    assert destination == mods.resolve() / "KittenIO"
    assert (destination / "mod.toml").is_file()
