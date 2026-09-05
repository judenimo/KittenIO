"""Safe, idempotent installation of a packaged KittenIO StarMap mod."""

from __future__ import annotations

import hashlib
import os
import re
import shutil
import stat
import tempfile
import zipfile
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from uuid import UUID, uuid4

_HASH_CHUNK_BYTES = 1024 * 1024
_MAX_ARCHIVE_ENTRIES = 4096
_MAX_ARCHIVE_ENTRY_BYTES = 64 * 1024 * 1024
_MAX_ARCHIVE_TOTAL_BYTES = 256 * 1024 * 1024
_MAX_ARCHIVE_COMPRESSION_RATIO = 200


class InstallError(RuntimeError):
    pass


def _is_link_or_reparse(path: Path) -> bool:
    try:
        if path.is_symlink():
            return True
        is_junction = getattr(path, "is_junction", None)
        if callable(is_junction) and is_junction():
            return True
        attributes = getattr(path.lstat(), "st_file_attributes", 0)
        return bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0))
    except OSError as exc:
        raise InstallError(f"cannot inspect artifact path {path}: {exc}") from exc


def _validate_unlinked_tree(root: Path) -> None:
    """Reject symlinks, junctions, and other reparse points without following them."""

    if _is_link_or_reparse(root):
        raise InstallError(f"artifact contains a linked or reparse path: {root}")
    for current_name, directories, files in os.walk(root, topdown=True, followlinks=False):
        current = Path(current_name)
        if _is_link_or_reparse(current):
            raise InstallError(f"artifact contains a linked or reparse path: {current}")
        for name in (*directories, *files):
            candidate = current / name
            if _is_link_or_reparse(candidate):
                raise InstallError(f"artifact contains a linked or reparse path: {candidate}")


def _reject_links_during_copy(directory: str, names: list[str]) -> set[str]:
    """copytree callback that rechecks each directory immediately before copying."""

    current = Path(directory)
    if _is_link_or_reparse(current):
        raise InstallError(f"artifact changed to a linked or reparse path: {current}")
    for name in names:
        candidate = current / name
        if _is_link_or_reparse(candidate):
            raise InstallError(f"artifact changed to a linked or reparse path: {candidate}")
    return set()


def default_mods_directory() -> Path:
    documents = _windows_documents_directory() if os.name == "nt" else Path.home() / "Documents"
    return documents / "My Games" / "Kitten Space Agency" / "mods"


def _windows_documents_directory() -> Path:
    """Resolve the redirected Windows Documents known folder (including OneDrive)."""

    import ctypes
    from ctypes import wintypes

    class Guid(ctypes.Structure):
        _fields_ = [
            ("data1", wintypes.DWORD),
            ("data2", wintypes.WORD),
            ("data3", wintypes.WORD),
            ("data4", ctypes.c_ubyte * 8),
        ]

    folder_id = Guid.from_buffer_copy(UUID("fdd39ad0-238f-46af-adb4-6c85480369c7").bytes_le)
    path_pointer = ctypes.c_wchar_p()
    shell32 = ctypes.windll.shell32
    ole32 = ctypes.windll.ole32
    shell32.SHGetKnownFolderPath.argtypes = [
        ctypes.POINTER(Guid),
        wintypes.DWORD,
        wintypes.HANDLE,
        ctypes.POINTER(ctypes.c_wchar_p),
    ]
    shell32.SHGetKnownFolderPath.restype = ctypes.c_long
    result = shell32.SHGetKnownFolderPath(ctypes.byref(folder_id), 0, None, ctypes.byref(path_pointer))
    if result != 0:
        raise OSError(result, "SHGetKnownFolderPath(FOLDERID_Documents) failed")
    try:
        if not path_pointer.value:
            raise OSError("Windows returned an empty Documents known-folder path")
        return Path(path_pointer.value)
    finally:
        ole32.CoTaskMemFree(path_pointer)


def _validate_source(root: Path) -> Path:
    if (root / "mod.toml").is_file():
        candidate = root
    else:
        nested = root / "KittenIO"
        if (nested / "mod.toml").is_file():
            candidate = nested
        else:
            children = [item for item in root.iterdir() if item.is_dir() and (item / "mod.toml").is_file()]
            if len(children) != 1:
                raise InstallError("artifact does not contain a KittenIO mod.toml")
            candidate = children[0]
    _validate_unlinked_tree(candidate)
    required = {
        "mod.toml",
        "0Harmony.dll",
        "KittenIO.dll",
        "KittenIO.Protocol.dll",
        "KittenIO.deps.json",
    }
    missing = [name for name in sorted(required) if not (candidate / name).is_file()]
    if missing:
        raise InstallError(f"artifact is missing required files: {', '.join(missing)}")
    forbidden = []
    for file in candidate.rglob("*"):
        if not file.is_file():
            continue
        lowered = file.name.casefold()
        if (
            lowered == "ksa.dll"
            or (lowered.startswith("starmap") and lowered.endswith(".dll"))
            or (lowered.startswith("brutal") and lowered.endswith(".dll"))
        ):
            forbidden.append(file.name)
    if forbidden:
        raise InstallError(f"artifact contains forbidden game/loader binaries: {', '.join(forbidden)}")
    return candidate


def _safe_extract(archive: Path, destination: Path) -> None:
    destination_resolved = destination.resolve()
    with zipfile.ZipFile(archive) as bundle:
        validated: list[tuple[zipfile.ZipInfo, PurePosixPath, bool]] = []
        destinations: dict[str, tuple[str, bool]] = {}
        entries = bundle.infolist()
        if len(entries) > _MAX_ARCHIVE_ENTRIES:
            raise InstallError(f"archive contains more than {_MAX_ARCHIVE_ENTRIES} entries")
        declared_total = 0
        for entry in entries:
            normalized = entry.filename.replace("\\", "/")
            posix = PurePosixPath(normalized)
            if (
                not posix.parts
                or posix.is_absolute()
                or ".." in posix.parts
                or any(not part or "\0" in part or ":" in part for part in posix.parts)
            ):
                raise InstallError(f"unsafe archive path: {entry.filename}")
            canonical = "/".join(posix.parts)
            collision_key = canonical.casefold()
            if collision_key in destinations:
                previous = destinations[collision_key][0]
                raise InstallError(
                    f"archive contains duplicate normalized destination: {previous} and {entry.filename}"
                )
            is_directory = entry.is_dir() or normalized.endswith("/")
            if is_directory and entry.file_size != 0:
                raise InstallError(f"archive directory entry contains data: {entry.filename}")
            if not is_directory:
                if entry.file_size > _MAX_ARCHIVE_ENTRY_BYTES:
                    raise InstallError(f"archive entry exceeds the uncompressed size limit: {entry.filename}")
                declared_total += entry.file_size
                if declared_total > _MAX_ARCHIVE_TOTAL_BYTES:
                    raise InstallError("archive exceeds the total uncompressed size limit")
                if entry.file_size and (
                    entry.compress_size == 0 or entry.file_size > entry.compress_size * _MAX_ARCHIVE_COMPRESSION_RATIO
                ):
                    raise InstallError(f"archive entry exceeds the permitted expansion ratio: {entry.filename}")
            destinations[collision_key] = (entry.filename, is_directory)
            validated.append((entry, posix, is_directory))

        # A file cannot also be the case-insensitive parent of another entry.
        # Check after admission so archive ordering can't change the result.
        for _, posix, _ in validated:
            for index in range(1, len(posix.parts)):
                parent_key = "/".join(posix.parts[:index]).casefold()
                parent = destinations.get(parent_key)
                if parent is not None and not parent[1]:
                    raise InstallError(f"archive destination has a file as its parent: {parent[0]}")

        extracted_total = 0
        for entry, posix, is_directory in validated:
            candidate = destination.joinpath(*posix.parts).resolve()
            try:
                candidate.relative_to(destination_resolved)
            except ValueError as exc:
                raise InstallError(f"unsafe archive path: {entry.filename}") from exc
            if is_directory:
                candidate.mkdir(parents=True, exist_ok=True)
                continue
            candidate.parent.mkdir(parents=True, exist_ok=True)
            with bundle.open(entry) as source, candidate.open("wb") as target:
                extracted_entry = 0
                while chunk := source.read(_HASH_CHUNK_BYTES):
                    extracted_entry += len(chunk)
                    extracted_total += len(chunk)
                    if extracted_entry > entry.file_size:
                        raise InstallError(f"archive entry expanded beyond its declared size: {entry.filename}")
                    if extracted_entry > _MAX_ARCHIVE_ENTRY_BYTES:
                        raise InstallError(f"archive entry exceeds the streamed size limit: {entry.filename}")
                    if extracted_total > _MAX_ARCHIVE_TOTAL_BYTES:
                        raise InstallError("archive exceeds the streamed uncompressed size limit")
                    target.write(chunk)
                if extracted_entry != entry.file_size:
                    raise InstallError(f"archive entry did not match its declared size: {entry.filename}")


def _file_digest(path: Path) -> bytes:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(_HASH_CHUNK_BYTES):
            digest.update(chunk)
    return digest.digest()


def _tree_fingerprint(root: Path) -> str | None:
    if not root.is_dir():
        return None
    _validate_unlinked_tree(root)
    files: list[Path] = []
    for current_name, directories, file_names in os.walk(root, topdown=True, followlinks=False):
        current = Path(current_name)
        if _is_link_or_reparse(current):
            raise InstallError(f"artifact contains a linked or reparse path: {current}")
        for name in directories:
            candidate = current / name
            if _is_link_or_reparse(candidate):
                raise InstallError(f"artifact contains a linked or reparse path: {candidate}")
        for name in file_names:
            candidate = current / name
            if _is_link_or_reparse(candidate):
                raise InstallError(f"artifact contains a linked or reparse path: {candidate}")
            if candidate.is_file():
                files.append(candidate)
    digest = hashlib.sha256()
    for file in sorted(files, key=str):
        if _is_link_or_reparse(file):
            raise InstallError(f"artifact contains a linked or reparse path: {file}")
        digest.update(file.relative_to(root).as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update(_file_digest(file))
    return digest.hexdigest()


def _unique_backup(root: Path, prefix: str, stamp: str) -> Path:
    candidate = root / f"{prefix}-{stamp}"
    counter = 1
    while candidate.exists():
        candidate = root / f"{prefix}-{stamp}-{counter}"
        counter += 1
    return candidate


def _paths_overlap(left: Path, right: Path) -> bool:
    """Return whether either resolved path contains the other."""

    left_resolved = left.resolve()
    right_resolved = right.resolve()
    return (
        left_resolved == right_resolved
        or left_resolved.is_relative_to(right_resolved)
        or right_resolved.is_relative_to(left_resolved)
    )


def _enable_manifest(manifest: Path, backup_root: Path, stamp: str) -> Path | None:
    """Enable only KittenIO's TOML block and preserve all unrelated text."""

    original = manifest.read_text(encoding="utf-8") if manifest.is_file() else ""
    lines = original.splitlines()
    blocks: list[tuple[int, int]] = []
    starts = [index for index, line in enumerate(lines) if line.strip() == "[[mods]]"]
    for offset, start in enumerate(starts):
        blocks.append((start, starts[offset + 1] if offset + 1 < len(starts) else len(lines)))
    id_pattern = re.compile(r"^\s*id\s*=\s*(['\"])KittenIO\1\s*(?:#.*)?$", re.IGNORECASE)
    enabled_pattern = re.compile(r"^\s*enabled\s*=", re.IGNORECASE)
    kitten_block: tuple[int, int] | None = None
    for start, end in blocks:
        if any(id_pattern.match(lines[index]) for index in range(start + 1, end)):
            kitten_block = (start, end)
            break
    if kitten_block is None:
        if lines and lines[-1] != "":
            lines.append("")
        lines.extend(["[[mods]]", 'id = "KittenIO"', "enabled = true"])
    else:
        start, end = kitten_block
        enabled_index = next((index for index in range(start + 1, end) if enabled_pattern.match(lines[index])), None)
        if enabled_index is None:
            lines.insert(start + 2, "enabled = true")
        elif re.match(r"^\s*enabled\s*=\s*true\s*(?:#.*)?$", lines[enabled_index], re.IGNORECASE):
            return None
        else:
            lines[enabled_index] = "enabled = true"
    updated = "\n".join(lines) + "\n"
    if updated == original:
        return None
    backup: Path | None = None
    if manifest.is_file():
        backup_root.mkdir(parents=True, exist_ok=True)
        backup = backup_root / f"manifest.toml.{stamp}.bak"
        counter = 1
        while backup.exists():
            backup = backup_root / f"manifest.toml.{stamp}.{counter}.bak"
            counter += 1
        shutil.copy2(manifest, backup)
    manifest.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(prefix=".manifest-", suffix=".tmp", dir=manifest.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(updated)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, manifest)
    finally:
        temporary.unlink(missing_ok=True)
    return backup


def install_mod(artifact: str | Path, mods_directory: str | Path | None = None) -> tuple[Path, Path | None]:
    source = Path(artifact).expanduser().resolve()
    if not source.exists():
        raise InstallError(f"artifact does not exist: {source}")
    mods = Path(mods_directory).expanduser().resolve() if mods_directory else default_mods_directory()
    destination = mods / "KittenIO"
    backup: Path | None = None
    user_root = mods.parent
    backup_root = user_root / "backups" / "KittenIO"
    manifest = user_root / "manifest.toml"
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")

    with tempfile.TemporaryDirectory(prefix="kittenio-install-") as temporary_name:
        temporary = Path(temporary_name)
        if source.is_dir():
            unpacked = _validate_source(source)
        elif source.suffix.lower() == ".zip":
            _safe_extract(source, temporary)
            unpacked = _validate_source(temporary)
        else:
            raise InstallError("artifact must be a directory or .zip release")

        if source.is_dir() and _paths_overlap(unpacked, destination):
            raise InstallError("directory artifact and KittenIO installation paths must not overlap")

        mods.mkdir(parents=True, exist_ok=True)

        staging: Path | None = None
        previous_moved = False
        installed_new_target = False
        try:
            if _tree_fingerprint(unpacked) != _tree_fingerprint(destination):
                backup_root.mkdir(parents=True, exist_ok=True)
                staging = mods / f".KittenIO.install-{uuid4().hex}"
                shutil.copytree(
                    unpacked,
                    staging,
                    symlinks=True,
                    ignore=_reject_links_during_copy,
                )
                _validate_unlinked_tree(staging)
                if destination.exists():
                    backup = _unique_backup(backup_root, "KittenIO", stamp)
                    os.replace(destination, backup)
                    previous_moved = True
                os.replace(staging, destination)
                installed_new_target = True
            _enable_manifest(manifest, backup_root, stamp)
        except BaseException:
            # Directory renames stay on the KSA user-data volume. Roll back both
            # a failed swap and a later manifest-update failure without ever
            # deleting the previously working installation first.
            if installed_new_target and destination.exists():
                assert staging is not None
                os.replace(destination, staging)
                installed_new_target = False
            if previous_moved and backup is not None and backup.exists() and not destination.exists():
                os.replace(backup, destination)
                backup = None
                previous_moved = False
            raise
        finally:
            if staging is not None and staging.exists():
                shutil.rmtree(staging)
    return destination, backup
