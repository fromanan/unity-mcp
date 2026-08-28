import os
import time
from typing import Any

from fastmcp import Context
from pydantic import BaseModel

from core.config import config
from models import MCPResponse
from services.registry import mcp_for_unity_resource
from services.tools import get_unity_instance_from_context
from services.state.external_changes_scanner import external_changes_scanner
from services.state.editor_state_store import editor_state_store
import transport.unity_transport as unity_transport
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.plugin_hub import PluginHub


class EditorStateUnity(BaseModel):
    instance_id: str | None = None
    unity_version: str | None = None
    project_id: str | None = None
    platform: str | None = None
    is_batch_mode: bool | None = None


class EditorStatePlayMode(BaseModel):
    is_playing: bool | None = None
    is_paused: bool | None = None
    is_changing: bool | None = None


class EditorStateActiveScene(BaseModel):
    path: str | None = None
    guid: str | None = None
    name: str | None = None


class EditorStateEditor(BaseModel):
    is_focused: bool | None = None
    play_mode: EditorStatePlayMode | None = None
    active_scene: EditorStateActiveScene | None = None


class EditorStateActivity(BaseModel):
    phase: str | None = None
    since_unix_ms: int | None = None
    reasons: list[str] | None = None


class EditorStateCompilation(BaseModel):
    is_compiling: bool | None = None
    is_domain_reload_pending: bool | None = None
    last_compile_started_unix_ms: int | None = None
    last_compile_finished_unix_ms: int | None = None
    last_domain_reload_before_unix_ms: int | None = None
    last_domain_reload_after_unix_ms: int | None = None


class EditorStateRefresh(BaseModel):
    is_refresh_in_progress: bool | None = None
    last_refresh_requested_unix_ms: int | None = None
    last_refresh_finished_unix_ms: int | None = None


class EditorStateAssets(BaseModel):
    is_updating: bool | None = None
    external_changes_dirty: bool | None = None
    external_changes_last_seen_unix_ms: int | None = None
    external_changes_dirty_since_unix_ms: int | None = None
    external_changes_last_cleared_unix_ms: int | None = None
    refresh: EditorStateRefresh | None = None


class EditorStateLastRun(BaseModel):
    finished_unix_ms: int | None = None
    result: str | None = None
    counts: Any | None = None


class EditorStateTests(BaseModel):
    is_running: bool | None = None
    mode: str | None = None
    current_job_id: str | None = None
    started_unix_ms: int | None = None
    started_by: str | None = None
    last_run: EditorStateLastRun | None = None


class EditorStateTransport(BaseModel):
    unity_bridge_connected: bool | None = None
    last_message_unix_ms: int | None = None
    last_editor_heartbeat_unix_ms: int | None = None
    last_editor_heartbeat_received_unix_ms: int | None = None


class EditorStateSettings(BaseModel):
    batch_execute_max_commands: int | None = None


class EditorStateAdvice(BaseModel):
    ready_for_tools: bool | None = None
    blocking_reasons: list[str] | None = None
    recommended_retry_after_ms: int | None = None
    recommended_next_action: str | None = None


class EditorStateStaleness(BaseModel):
    age_ms: int | None = None
    is_stale: bool | None = None
    basis: str | None = None


class EditorStateData(BaseModel):
    schema_version: str
    observed_at_unix_ms: int
    served_at_unix_ms: int | None = None
    sequence: int
    unity: EditorStateUnity | None = None
    editor: EditorStateEditor | None = None
    activity: EditorStateActivity | None = None
    compilation: EditorStateCompilation | None = None
    assets: EditorStateAssets | None = None
    tests: EditorStateTests | None = None
    transport: EditorStateTransport | None = None
    settings: EditorStateSettings | None = None
    advice: EditorStateAdvice | None = None
    staleness: EditorStateStaleness | None = None


def _now_unix_ms() -> int:
    return int(time.time() * 1000)


def _in_pytest() -> bool:
    # Avoid instance-discovery side effects during the Python integration test suite.
    return bool(os.environ.get("PYTEST_CURRENT_TEST"))


async def infer_single_instance_id(ctx: Context) -> str | None:
    """
    Best-effort: if exactly one Unity instance is connected, return its Name@hash id.
    This makes editor_state outputs self-describing even when no explicit active instance is set.
    """
    await ctx.info("If exactly one Unity instance is connected, return its Name@hash id.")

    transport = (config.transport_mode or "stdio").lower()

    if transport == "http":
        # HTTP/WebSocket transport: derive from PluginHub sessions.
        try:
            # In remote-hosted mode, filter sessions by user_id
            user_id = (await ctx.get_state(
                "user_id")) if config.http_remote_hosted else None
            sessions_data = await PluginHub.get_sessions(user_id=user_id)
            sessions = sessions_data.sessions if hasattr(
                sessions_data, "sessions") else {}
            if isinstance(sessions, dict) and len(sessions) == 1:
                session = next(iter(sessions.values()))
                project = getattr(session, "project", None)
                project_hash = getattr(session, "hash", None)
                if project and project_hash:
                    return f"{project}@{project_hash}"
        except Exception:
            return None
        return None

    # Stdio/TCP transport: derive from connection pool discovery.
    try:
        from transport.legacy.unity_connection import get_unity_connection_pool

        pool = get_unity_connection_pool()
        instances = pool.discover_all_instances(force_refresh=False)
        if isinstance(instances, list) and len(instances) == 1:
            inst = instances[0]
            inst_id = getattr(inst, "id", None)
            return str(inst_id) if inst_id else None
    except Exception:
        return None
    return None


def _enrich_advice_and_staleness(
    state_v2: dict[str, Any],
    *,
    freshness_unix_ms: int | None = None,
    freshness_basis: str = "observed_at_unix_ms",
    stale_after_ms: int = 2000,
) -> dict[str, Any]:
    now_ms = _now_unix_ms()
    observed = freshness_unix_ms
    if observed is None:
        observed = state_v2.get("observed_at_unix_ms")
    try:
        observed_ms = int(observed)
    except Exception:
        observed_ms = now_ms

    age_ms = max(0, now_ms - observed_ms)
    is_stale = age_ms > max(1, int(stale_after_ms))

    compilation = state_v2.get("compilation") or {}
    tests = state_v2.get("tests") or {}
    assets = state_v2.get("assets") or {}
    refresh = (assets.get("refresh") or {}) if isinstance(assets, dict) else {}

    blocking: list[str] = []
    if compilation.get("is_compiling") is True:
        blocking.append("compiling")
    if compilation.get("is_domain_reload_pending") is True:
        blocking.append("domain_reload")
    if tests.get("is_running") is True:
        blocking.append("running_tests")
    if refresh.get("is_refresh_in_progress") is True:
        blocking.append("asset_refresh")
    if is_stale:
        blocking.append("stale_status")

    ready_for_tools = len(blocking) == 0

    state_v2["advice"] = {
        "ready_for_tools": ready_for_tools,
        "blocking_reasons": blocking,
        "recommended_retry_after_ms": 0 if ready_for_tools else 500,
        "recommended_next_action": "none" if ready_for_tools else "retry_later",
    }
    state_v2["staleness"] = {
        "age_ms": age_ms,
        "is_stale": is_stale,
        "basis": freshness_basis,
    }
    return state_v2


@mcp_for_unity_resource(
    uri="mcpforunity://editor/state",
    name="editor_state",
    description=(
        "Canonical editor readiness snapshot. Unity proactively publishes changed "
        "snapshots and a main-thread heartbeat after capability negotiation, while "
        "the server serves a defensive per-instance cache. observed_at_unix_ms remains "
        "the semantic snapshot time, served_at_unix_ms is the response time, and "
        "staleness.basis identifies the freshness signal used. External-change status "
        "comes from an event-driven background watcher with periodic reconciliation "
        "rather than a recursive scan during this resource request."
    ),
)
async def get_editor_state(ctx: Context) -> MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)
    if not unity_instance and not _in_pytest():
        unity_instance = await infer_single_instance_id(ctx)

    cached_record = editor_state_store.get(
        unity_instance) if unity_instance else None
    is_proactive_cache = cached_record is not None and cached_record.state is not None

    if is_proactive_cache:
        state_v2 = cached_record.state or {}
        freshness_unix_ms = (
            cached_record.last_editor_heartbeat_received_unix_ms
            or cached_record.last_state_received_unix_ms
        )
        freshness_basis = (
            "editor_main_thread_heartbeat"
            if cached_record.last_editor_heartbeat_received_unix_ms is not None
            else "state_received"
        )
    else:
        response = await unity_transport.send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "get_editor_state",
            {},
        )

        # If Unity returns a structured retry hint or error, surface it directly.
        if isinstance(response, dict) and not response.get("success", True):
            return MCPResponse(**response)

        state_v2 = response.get("data") if isinstance(
            response, dict) and isinstance(response.get("data"), dict) else {}
        freshness_unix_ms = _now_unix_ms()
        freshness_basis = "direct_unity_response"

    state_v2.setdefault("schema_version", "unity-mcp/editor_state@2")
    state_v2.setdefault("observed_at_unix_ms", _now_unix_ms())
    state_v2.setdefault("sequence", 0)
    state_v2["served_at_unix_ms"] = _now_unix_ms()

    # Ensure the returned snapshot is clearly associated with the targeted instance.
    unity_section = state_v2.get("unity")
    if not isinstance(unity_section, dict):
        unity_section = {}
        state_v2["unity"] = unity_section
    current_instance_id = unity_section.get("instance_id")
    if current_instance_id in (None, ""):
        if unity_instance:
            unity_section["instance_id"] = unity_instance

    # External change detection (server-side): compute per instance based on project root path.
    try:
        instance_id = unity_section.get("instance_id")
        if isinstance(instance_id, str) and instance_id.strip():
            from services.resources.project_info import (
                get_cached_project_root,
                get_project_info,
            )

            project_root = editor_state_store.get_project_root(instance_id)
            if not project_root:
                project_root = get_cached_project_root(instance_id)
            if not project_root:
                proj_resp = await get_project_info(ctx)
                proj = proj_resp.model_dump() if hasattr(
                    proj_resp, "model_dump") else proj_resp
                proj_data = proj.get("data") if isinstance(proj, dict) else None
                project_root = proj_data.get("projectRoot") if isinstance(
                    proj_data, dict) else None
            if isinstance(project_root, str) and project_root.strip():
                external_changes_scanner.set_project_root(
                    instance_id, project_root)
                if cached_record is None:
                    await external_changes_scanner.start_tracking(
                        instance_id,
                        project_root,
                        f"resource:{instance_id}",
                    )

            ext = external_changes_scanner.get_snapshot(instance_id)

            assets = state_v2.get("assets")
            if not isinstance(assets, dict):
                assets = {}
                state_v2["assets"] = assets
            assets["external_changes_dirty"] = bool(
                ext.get("external_changes_dirty", False))
            assets["external_changes_last_seen_unix_ms"] = ext.get(
                "external_changes_last_seen_unix_ms")
            assets["external_changes_dirty_since_unix_ms"] = ext.get(
                "dirty_since_unix_ms")
            assets["external_changes_last_cleared_unix_ms"] = ext.get(
                "last_cleared_unix_ms")
    except Exception:
        pass

    transport = state_v2.get("transport")
    if not isinstance(transport, dict):
        transport = {}
        state_v2["transport"] = transport
    transport["unity_bridge_connected"] = True
    if cached_record is not None:
        transport["last_message_unix_ms"] = cached_record.last_message_received_unix_ms
        transport["last_editor_heartbeat_unix_ms"] = (
            cached_record.last_editor_heartbeat_unix_ms)
        transport["last_editor_heartbeat_received_unix_ms"] = (
            cached_record.last_editor_heartbeat_received_unix_ms)

    state_v2 = _enrich_advice_and_staleness(
        state_v2,
        freshness_unix_ms=freshness_unix_ms,
        freshness_basis=freshness_basis,
        stale_after_ms=2500,
    )

    try:
        if hasattr(EditorStateData, "model_validate"):
            validated = EditorStateData.model_validate(state_v2)
        else:
            validated = EditorStateData.parse_obj(
                state_v2)  # type: ignore[attr-defined]
        data = validated.model_dump() if hasattr(
            validated, "model_dump") else validated.dict()
    except Exception as e:
        return MCPResponse(
            success=False,
            error="invalid_editor_state",
            message=f"Editor state payload failed validation: {e}",
            data={"raw": state_v2},
        )

    return MCPResponse(success=True, message="Retrieved editor state.", data=data)
