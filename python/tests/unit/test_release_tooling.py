from __future__ import annotations

import hashlib
from pathlib import Path

import pytest

from scripts import dev


@pytest.mark.parametrize(
    "source",
    [
        "pyproject.toml",
        "mod.toml",
        "Directory.Build.props",
        "kittenio.__version__",
        "CHANGELOG.md",
    ],
)
def test_every_release_version_source_must_match(source: str) -> None:
    values = {
        "pyproject.toml": "0.1.0",
        "mod.toml": "0.1.0",
        "Directory.Build.props": "0.1.0",
        "kittenio.__version__": "0.1.0",
        "CHANGELOG.md": "0.1.0",
    }
    values[source] = "0.1.1"
    with pytest.raises(dev.DevError, match=source.replace(".", r"\.")):
        dev._validate_release_version_values(values)


@pytest.mark.parametrize("description", ["KSA reference assembly", "StarMap API assembly"])
def test_wrong_pinned_game_or_loader_hash_fails(tmp_path: Path, description: str) -> None:
    candidate = tmp_path / "candidate.dll"
    candidate.write_bytes(b"wrong release input")
    with pytest.raises(dev.DevError, match="SHA-256"):
        dev._validate_pinned_file(
            candidate,
            description=description,
            expected_sha256="0" * 64,
        )


@pytest.mark.parametrize("description", ["KSA reference assembly", "StarMap API assembly"])
def test_wrong_pinned_game_or_loader_version_fails(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    description: str,
) -> None:
    candidate = tmp_path / "candidate.dll"
    content = b"deterministic test assembly"
    candidate.write_bytes(content)
    monkeypatch.setattr(dev, "_windows_product_version", lambda _path: "wrong-version")
    with pytest.raises(dev.DevError, match="product version"):
        dev._validate_pinned_file(
            candidate,
            description=description,
            expected_sha256=hashlib.sha256(content).hexdigest().upper(),
            expected_versions=frozenset({"expected-version"}),
        )
