import asyncio
import os
import time
from pathlib import Path

import pytest


def test_external_changes_scanner_marks_dirty_and_clears(tmp_path, monkeypatch):
    # Ensure the scanner is active for this unit-style test (not gated by PYTEST_CURRENT_TEST).
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    # Create a minimal Unity-like layout
    root = tmp_path / "Project"
    (root / "Assets").mkdir(parents=True)
    (root / "ProjectSettings").mkdir(parents=True)
    (root / "Packages").mkdir(parents=True)

    inst = "Test@deadbeef"
    s = ExternalChangesScanner(scan_interval_ms=0, max_entries=10000)
    s.set_project_root(inst, str(root))

    # Create a file before baseline so the initial scan establishes a stable reference point.
    p = root / "Assets" / "x.txt"
    p.write_text("hi")

    # Baseline scan: should not be dirty.
    first = s.update_and_get(inst)
    assert first["external_changes_dirty"] is False

    # Touch the file and scan again: should become dirty.
    now = time.time()
    os.utime(p, (now + 10.0, now + 10.0))

    second = s.update_and_get(inst)
    assert second["external_changes_dirty"] is True
    assert isinstance(second["external_changes_last_seen_unix_ms"], int)
    assert isinstance(second["dirty_since_unix_ms"], int)

    # Clear and confirm dirty flag resets.
    s.clear_dirty(inst)
    third = s.update_and_get(inst)
    assert third["external_changes_dirty"] is False
    assert isinstance(third["last_cleared_unix_ms"], int)


def test_external_changes_scanner_includes_file_dependency_roots(tmp_path, monkeypatch):
    # Ensure the scanner is active for this unit-style test (not gated by PYTEST_CURRENT_TEST).
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    # Unity project root
    root = tmp_path / "Project"
    (root / "Assets").mkdir(parents=True)
    (root / "ProjectSettings").mkdir(parents=True)
    (root / "Packages").mkdir(parents=True)

    # External local package root (outside project root)
    pkg = tmp_path / "ExternalPkg"
    (pkg / "Editor").mkdir(parents=True)
    target = pkg / "Editor" / "Some.cs"
    target.write_text("// v1")

    # manifest.json referencing file: dependency
    manifest = root / "Packages" / "manifest.json"
    manifest.write_text(
        '{\n  "dependencies": {\n    "com.example.pkg": "file:../../ExternalPkg"\n  }\n}\n',
        encoding="utf-8",
    )

    inst = "Test@deadbeef"
    s = ExternalChangesScanner(scan_interval_ms=0, max_entries=10000)
    s.set_project_root(inst, str(root))

    # Baseline scan captures current mtimes across project + external pkg
    baseline = s.update_and_get(inst)
    assert baseline["external_changes_dirty"] is False

    # Touch external package file and scan again -> should mark dirty
    now = time.time()
    os.utime(target, (now + 10.0, now + 10.0))

    changed = s.update_and_get(inst)
    assert changed["external_changes_dirty"] is True


def test_external_changes_snapshot_never_scans(tmp_path, monkeypatch):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    root = tmp_path / "Project"
    (root / "Assets").mkdir(parents=True)
    scanner = ExternalChangesScanner(scan_interval_ms=0)
    scanner.set_project_root("Test@cached", str(root))

    def fail_scan(_roots):
        raise AssertionError("get_snapshot must not scan the filesystem")

    monkeypatch.setattr(scanner, "_scan_paths_max_mtime_ns", fail_scan)
    snapshot = scanner.get_snapshot("Test@cached")
    assert snapshot["external_changes_dirty"] is False


@pytest.mark.asyncio
async def test_external_changes_background_watcher_marks_dirty(tmp_path, monkeypatch):
    monkeypatch.delenv("PYTEST_CURRENT_TEST", raising=False)

    from services.state.external_changes_scanner import ExternalChangesScanner

    root = tmp_path / "Project"
    assets = root / "Assets"
    assets.mkdir(parents=True)
    (root / "ProjectSettings").mkdir()
    packages = root / "Packages"
    packages.mkdir()
    (packages / "manifest.json").write_text(
        '{"dependencies": {}}', encoding="utf-8")

    instance_id = "Test@watcher"
    session_id = "session-watcher"
    scanner = ExternalChangesScanner(
        scan_interval_ms=0,
        reconcile_interval_seconds=1.0,
    )
    await scanner.start_tracking(instance_id, str(root), session_id)

    try:
        deadline = time.monotonic() + 5.0
        while scanner._get_state(instance_id).last_seen_mtime_ns is None:
            assert time.monotonic() < deadline
            await asyncio.sleep(0.05)

        (assets / "Changed.txt").write_text("changed", encoding="utf-8")
        while not scanner.get_snapshot(instance_id)["external_changes_dirty"]:
            assert time.monotonic() < deadline
            await asyncio.sleep(0.05)
    finally:
        await scanner.stop_tracking(instance_id, session_id)
