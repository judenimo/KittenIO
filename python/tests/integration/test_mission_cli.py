import sys

import kittenio.cli as cli
import pytest
from kittenio import ConnectionTimeoutError, MissionInterruptedError, WaitTimeoutError


def test_new_creates_a_loadable_telemetry_mission_and_preserves_existing_files(tmp_path, capsys):
    target = tmp_path / "flight scripts" / "my mission.py"
    assert cli.main(["new", str(target)]) == 0
    source = target.read_text(encoding="utf-8")
    assert cli.load_program(str(target)).__name__ == "mission"
    assert "Created telemetry mission" in capsys.readouterr().out
    assert cli.main(["new", str(target)]) == 2
    assert target.read_text(encoding="utf-8") == source
    assert "already exists" in capsys.readouterr().err


def test_new_defaults_to_mission_py(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    assert cli.main(["new"]) == 0
    assert (tmp_path / "mission.py").is_file()


def test_new_requires_a_python_file(tmp_path, capsys):
    assert cli.main(["new", str(tmp_path / "notes.txt")]) == 2
    assert "must end in .py" in capsys.readouterr().err
    assert not (tmp_path / "notes.txt").exists()


def test_file_mission_imports_siblings_at_load_and_during_execution(tmp_path, monkeypatch):
    directory = tmp_path / "scripts with spaces"
    directory.mkdir()
    (directory / "mission_helper_eager.py").write_text("VALUE = 12\n", encoding="utf-8")
    (directory / "mission_helper_lazy.py").write_text("VALUE = 30\n", encoding="utf-8")
    mission = directory / "mission.py"
    mission.write_text(
        "from mission_helper_eager import VALUE\n"
        "def mission(ksa):\n"
        "    from mission_helper_lazy import VALUE as other\n"
        "    ksa.append(VALUE + other)\n",
        encoding="utf-8",
    )
    values = []
    original_path = sys.path[:]

    def fake_run(program, **kwargs):
        assert kwargs["connect_timeout"] == 7
        program(values)

    monkeypatch.setattr(cli, "run", fake_run)
    try:
        assert cli.main([str(mission), "--connect-timeout", "7"]) == 0
        assert values == [42]
        assert sys.path == original_path
    finally:
        sys.modules.pop("mission_helper_eager", None)
        sys.modules.pop("mission_helper_lazy", None)


@pytest.mark.parametrize("body", ["def mission(): pass", "async def mission(ksa): pass"])
def test_invalid_function_missions_fail_before_connecting(tmp_path, body):
    path = tmp_path / "mission.py"
    path.write_text(body, encoding="utf-8")
    with pytest.raises(TypeError, match="mission must"):
        cli.load_program(str(path))


def test_explicit_named_function_and_ambiguous_missions(tmp_path):
    path = tmp_path / "mission.py"
    path.write_text(
        "from kittenio import KittenProgram\n"
        "def mission(ksa): pass\n"
        "def descent(ksa): pass\n"
        "class Ascent(KittenProgram): pass\n",
        encoding="utf-8",
    )
    with pytest.raises(TypeError, match="select one"):
        cli.load_program(str(path))
    assert cli.load_program(str(path), "descent").__name__ == "descent"
    assert cli.load_program(str(path), "Ascent").__name__ == "Ascent"


@pytest.mark.parametrize(
    "error",
    [
        ConnectionTimeoutError("launch KSA"),
        MissionInterruptedError("rerun mission"),
        WaitTimeoutError("waiting for touchdown"),
    ],
)
def test_expected_mission_failures_have_short_errors_and_nonzero_exit(tmp_path, monkeypatch, capsys, error):
    path = tmp_path / "mission.py"
    path.write_text("def mission(ksa): pass\n", encoding="utf-8")

    def fake_run(*args, **kwargs):
        raise error

    monkeypatch.setattr(cli, "run", fake_run)
    assert cli.main([str(path)]) == 1
    assert str(error) in capsys.readouterr().err


def test_syntax_errors_show_file_and_line(tmp_path, capsys):
    path = tmp_path / "mission.py"
    path.write_text("def mission(ksa)\n    pass\n", encoding="utf-8")
    assert cli.main([str(path)]) == 2
    assert f"{path}:1:" in capsys.readouterr().err
