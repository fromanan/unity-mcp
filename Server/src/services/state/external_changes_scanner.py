from __future__ import annotations

import asyncio
import json
import os
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from watchfiles import awatch


def _now_unix_ms() -> int:
    return int(time.time() * 1000)


@dataclass
class ExternalChangesState:
    project_root: str | None = None
    last_scan_unix_ms: int | None = None
    last_seen_mtime_ns: int | None = None
    dirty: bool = False
    dirty_since_unix_ms: int | None = None
    external_changes_last_seen_unix_ms: int | None = None
    last_cleared_unix_ms: int | None = None
    # Cached package roots referenced by Packages/manifest.json "file:" dependencies
    extra_roots: list[str] | None = None
    manifest_last_mtime_ns: int | None = None
    owner_session_id: str | None = None
    generation: int = 0
    tracker_task: asyncio.Task[None] | None = None


class ExternalChangesScanner:
    """Event-driven external-change tracker with bounded background reconciliation."""

    def __init__(
        self,
        *,
        scan_interval_ms: int = 1500,
        max_entries: int = 20000,
        reconcile_interval_seconds: float = 300.0,
    ) -> None:
        self._states: dict[str, ExternalChangesState] = {}
        self._scan_interval_ms = int(scan_interval_ms)
        self._max_entries = int(max_entries)
        self._reconcile_interval_seconds = max(
            1.0, float(reconcile_interval_seconds))
        self._lock = threading.RLock()

    def _get_state(self, instance_id: str) -> ExternalChangesState:
        with self._lock:
            return self._states.setdefault(instance_id, ExternalChangesState())

    def set_project_root(self, instance_id: str, project_root: str | None) -> None:
        if not project_root:
            return
        normalized = str(Path(project_root).resolve())
        with self._lock:
            state = self._states.setdefault(instance_id, ExternalChangesState())
            if state.project_root != normalized:
                state.project_root = normalized
                state.last_seen_mtime_ns = None
                state.extra_roots = None
                state.manifest_last_mtime_ns = None

    async def start_tracking(
        self,
        instance_id: str,
        project_root: str | None,
        owner_session_id: str,
    ) -> None:
        if not project_root:
            return

        normalized = str(Path(project_root).resolve())
        previous_task: asyncio.Task[None] | None = None
        with self._lock:
            state = self._states.setdefault(instance_id, ExternalChangesState())
            if (
                state.project_root == normalized
                and state.owner_session_id == owner_session_id
                and state.tracker_task is not None
                and not state.tracker_task.done()
            ):
                return

            previous_task = state.tracker_task
            state.generation += 1
            generation = state.generation
            state.project_root = normalized
            state.owner_session_id = owner_session_id
            state.last_seen_mtime_ns = None
            state.extra_roots = None
            state.manifest_last_mtime_ns = None
            state.tracker_task = None

        if previous_task is not None and not previous_task.done():
            previous_task.cancel()
            await asyncio.gather(previous_task, return_exceptions=True)

        task = asyncio.create_task(
            self._tracking_loop(instance_id, generation),
            name=f"unity-mcp-external-changes-{instance_id}",
        )
        with self._lock:
            state = self._states.get(instance_id)
            if state is None or state.generation != generation:
                task.cancel()
            else:
                state.tracker_task = task

    async def stop_tracking(
        self,
        instance_id: str,
        owner_session_id: str | None = None,
    ) -> None:
        task: asyncio.Task[None] | None = None
        with self._lock:
            state = self._states.get(instance_id)
            if state is None:
                return
            if owner_session_id is not None and state.owner_session_id != owner_session_id:
                return

            state.generation += 1
            task = state.tracker_task
            state.tracker_task = None
            state.owner_session_id = None

        if task is not None and not task.done():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)

    async def shutdown(self) -> None:
        tasks: list[asyncio.Task[None]] = []
        with self._lock:
            for state in self._states.values():
                state.generation += 1
                if state.tracker_task is not None and not state.tracker_task.done():
                    state.tracker_task.cancel()
                    tasks.append(state.tracker_task)
                state.tracker_task = None
                state.owner_session_id = None

        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)

    def clear_dirty(self, instance_id: str) -> None:
        with self._lock:
            state = self._states.setdefault(instance_id, ExternalChangesState())
            state.dirty = False
            state.dirty_since_unix_ms = None
            state.last_cleared_unix_ms = _now_unix_ms()
            # Reset baseline in a background reconciliation so the request path remains cheap.
            state.last_seen_mtime_ns = None
            generation = state.generation
            is_tracking = state.tracker_task is not None and not state.tracker_task.done()

        if is_tracking:
            try:
                loop = asyncio.get_running_loop()
                loop.create_task(self._reconcile(instance_id, generation))
            except RuntimeError:
                pass

    def get_snapshot(self, instance_id: str) -> dict[str, int | bool | None]:
        with self._lock:
            state = self._states.setdefault(instance_id, ExternalChangesState())
            return self._state_payload(state)

    def update_and_get(self, instance_id: str) -> dict[str, int | bool | None]:
        """Compatibility path for explicit callers; normal editor-state reads use get_snapshot."""
        now = _now_unix_ms()
        with self._lock:
            state = self._states.setdefault(instance_id, ExternalChangesState())
            if (
                state.last_scan_unix_ms is not None
                and now - state.last_scan_unix_ms < self._scan_interval_ms
            ):
                return self._state_payload(state)
            state.last_scan_unix_ms = now

        self.scan_now(instance_id)
        return self.get_snapshot(instance_id)

    def scan_now(self, instance_id: str) -> dict[str, int | bool | None]:
        with self._lock:
            state = self._states.setdefault(instance_id, ExternalChangesState())
            project_root = state.project_root
            generation = state.generation

        if not project_root:
            return self.get_snapshot(instance_id)

        newest = self._scan_paths_max_mtime_ns(
            self._get_watch_roots(instance_id, generation))
        self._apply_reconciliation(instance_id, generation, newest)
        return self.get_snapshot(instance_id)

    async def _tracking_loop(self, instance_id: str, generation: int) -> None:
        try:
            await self._reconcile(instance_id, generation)

            while self._is_current_generation(instance_id, generation):
                roots = self._get_watch_roots(instance_id, generation)
                if not roots:
                    await asyncio.sleep(1.0)
                    continue

                try:
                    async for changes in awatch(
                        *roots,
                        debounce=200,
                        step=50,
                        rust_timeout=int(self._reconcile_interval_seconds * 1000),
                        yield_on_timeout=True,
                        ignore_permission_denied=True,
                    ):
                        if not self._is_current_generation(instance_id, generation):
                            return

                        manifest_changed = False
                        if changes:
                            manifest_changed = self._mark_changes(
                                instance_id, generation, changes)
                        else:
                            await self._reconcile(instance_id, generation)

                        if manifest_changed:
                            break
                except asyncio.CancelledError:
                    raise
                except (FileNotFoundError, OSError):
                    await asyncio.sleep(1.0)
        except asyncio.CancelledError:
            raise

    async def _reconcile(self, instance_id: str, generation: int) -> None:
        roots = self._get_watch_roots(instance_id, generation)
        if not roots:
            return
        newest = await asyncio.to_thread(self._scan_paths_max_mtime_ns, roots)
        self._apply_reconciliation(instance_id, generation, newest)

    def _apply_reconciliation(
        self,
        instance_id: str,
        generation: int,
        newest: int | None,
    ) -> None:
        if newest is None:
            return

        now = _now_unix_ms()
        with self._lock:
            state = self._states.get(instance_id)
            if state is None or state.generation != generation:
                return

            state.last_scan_unix_ms = now
            if state.last_seen_mtime_ns is None:
                state.last_seen_mtime_ns = newest
            elif newest > state.last_seen_mtime_ns:
                state.last_seen_mtime_ns = newest
                self._mark_dirty(state, now)

    def _mark_changes(
        self,
        instance_id: str,
        generation: int,
        changes: set[tuple[object, str]],
    ) -> bool:
        now = _now_unix_ms()
        with self._lock:
            state = self._states.get(instance_id)
            if state is None or state.generation != generation:
                return False
            self._mark_dirty(state, now)
            project_root = state.project_root

        if not project_root:
            return False
        manifest = str((Path(project_root) / "Packages" / "manifest.json").resolve())
        return any(str(Path(path).resolve()) == manifest for _, path in changes)

    @staticmethod
    def _mark_dirty(state: ExternalChangesState, now: int) -> None:
        state.external_changes_last_seen_unix_ms = now
        if not state.dirty:
            state.dirty = True
            state.dirty_since_unix_ms = now

    def _get_watch_roots(self, instance_id: str, generation: int) -> list[Path]:
        with self._lock:
            state = self._states.get(instance_id)
            if (
                state is None
                or state.generation != generation
                or not state.project_root
            ):
                return []
            project_root = Path(state.project_root)
            extra_roots = self._resolve_manifest_extra_roots(project_root, state)

        roots = [
            project_root / "Assets",
            project_root / "ProjectSettings",
            project_root / "Packages",
            *extra_roots,
        ]
        return [root for root in roots if root.exists() and root.is_dir()]

    def _is_current_generation(self, instance_id: str, generation: int) -> bool:
        with self._lock:
            state = self._states.get(instance_id)
            return state is not None and state.generation == generation

    @staticmethod
    def _state_payload(state: ExternalChangesState) -> dict[str, int | bool | None]:
        return {
            "external_changes_dirty": state.dirty,
            "external_changes_last_seen_unix_ms": state.external_changes_last_seen_unix_ms,
            "dirty_since_unix_ms": state.dirty_since_unix_ms,
            "last_cleared_unix_ms": state.last_cleared_unix_ms,
        }

    def _scan_paths_max_mtime_ns(self, roots: Iterable[Path]) -> int | None:
        newest: int | None = None
        entries = 0

        for root in roots:
            if not root.exists():
                continue

            # Walk the tree; skip common massive/irrelevant dirs (Library/Temp/Logs).
            for dirpath, dirnames, filenames in os.walk(str(root)):
                entries += 1
                if entries > self._max_entries:
                    return newest

                directory = Path(dirpath)
                name = directory.name.lower()
                if name in {"library", "temp", "logs", "obj", ".git", "node_modules"}:
                    dirnames[:] = []
                    continue

                # Allow skipping hidden directories quickly
                dirnames[:] = [name for name in dirnames if not name.startswith(".")]

                for filename in filenames:
                    if filename.startswith("."):
                        continue
                    entries += 1
                    if entries > self._max_entries:
                        return newest
                    path = directory / filename
                    try:
                        stat = path.stat()
                    except OSError:
                        continue
                    modified = getattr(stat, "st_mtime_ns", None)
                    if modified is None:
                        modified = int(stat.st_mtime * 1_000_000_000)
                    newest = modified if newest is None else max(newest, int(modified))

        return newest

    def _resolve_manifest_extra_roots(
        self,
        project_root: Path,
        state: ExternalChangesState,
    ) -> list[Path]:
        """Resolve existing local ``file:`` package dependency roots."""
        manifest_path = project_root / "Packages" / "manifest.json"
        try:
            stat = manifest_path.stat()
        except OSError:
            state.extra_roots = []
            state.manifest_last_mtime_ns = None
            return []

        modified = getattr(
            stat, "st_mtime_ns", int(stat.st_mtime * 1_000_000_000))
        if state.extra_roots is not None and state.manifest_last_mtime_ns == modified:
            return [Path(path) for path in state.extra_roots if path]

        try:
            document = json.loads(manifest_path.read_text(encoding="utf-8"))
        except Exception:
            state.extra_roots = []
            state.manifest_last_mtime_ns = modified
            return []

        dependencies = document.get("dependencies") if isinstance(document, dict) else None
        if not isinstance(dependencies, dict):
            state.extra_roots = []
            state.manifest_last_mtime_ns = modified
            return []

        roots: list[str] = []
        base_dir = manifest_path.parent
        for version in dependencies.values():
            if not isinstance(version, str):
                continue
            value = version.strip()
            if not value.startswith("file:"):
                continue
            suffix = value[len("file:"):].strip()
            if suffix.startswith("///"):
                candidate = Path("/" + suffix.lstrip("/"))
            elif suffix.startswith("/"):
                candidate = Path(suffix)
            else:
                candidate = (base_dir / suffix).resolve()
            try:
                if candidate.exists() and candidate.is_dir():
                    roots.append(str(candidate))
            except OSError:
                continue

        deduped: list[str] = []
        seen: set[str] = set()
        for root in roots:
            if root not in seen:
                seen.add(root)
                deduped.append(root)

        state.extra_roots = deduped
        state.manifest_last_mtime_ns = modified
        return [Path(path) for path in deduped]


external_changes_scanner = ExternalChangesScanner()
