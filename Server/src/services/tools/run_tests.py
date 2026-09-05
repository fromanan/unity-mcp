"""Async Unity Test Runner jobs: start + poll."""
from __future__ import annotations

import asyncio
import logging
import time
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations
from pydantic import BaseModel

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
import transport.unity_transport as unity_transport
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.plugin_hub import PluginHub
from utils.focus_nudge import nudge_unity_focus, should_nudge, reset_nudge_backoff

logger = logging.getLogger(__name__)

MIN_INIT_TIMEOUT_MS = 1_000
MAX_INIT_TIMEOUT_MS = 600_000
DEFAULT_EDITMODE_INIT_TIMEOUT_MS = 15_000
DEFAULT_PLAYMODE_INIT_TIMEOUT_MS = 120_000

# Strong references to background fire-and-forget tasks to prevent premature GC.
_background_tasks: set[asyncio.Task] = set()


async def _get_unity_project_path(unity_instance: str | None) -> str | None:
    """Get the project root path for a Unity instance (for focus nudging).

    Args:
        unity_instance: Unity instance hash or "Name@hash" format or None

    Returns:
        Project root path (e.g., "/Users/name/project"), or falls back to project_name if path unavailable
    """
    if not unity_instance:
        return None

    try:
        registry = PluginHub._registry
        if not registry:
            return None

        # Parse Name@hash format if present (middleware stores instances as "Name@hash")
        target_hash = unity_instance
        if "@" in target_hash:
            _, _, target_hash = target_hash.rpartition("@")
        if not target_hash:
            return None

        # Get session by hash
        session_id = await registry.get_session_id_by_hash(target_hash)
        if not session_id:
            return None

        session = await registry.get_session(session_id)
        if not session:
            return None

    except Exception as e:
        # Re-raise cancellation errors so task cancellation propagates
        if isinstance(e, asyncio.CancelledError):
            raise
        logger.debug(f"Could not get Unity project path: {e}")
        return None
    else:
        # Return full path if available, otherwise fall back to project name
        if session.project_path:
            return session.project_path
        return session.project_name if session.project_name else None


class RunTestsSummary(BaseModel):
    total: int
    passed: int
    failed: int
    skipped: int
    durationSeconds: float
    resultState: str


class RunTestsTestResult(BaseModel):
    name: str
    fullName: str
    state: str
    durationSeconds: float
    message: str | None = None
    stackTrace: str | None = None
    output: str | None = None


class RunTestsResult(BaseModel):
    mode: str
    summary: RunTestsSummary
    results: list[RunTestsTestResult] | None = None


class RunTestsStartData(BaseModel):
    job_id: str
    status: str
    mode: str | None = None
    effective_init_timeout_ms: int | None = None
    include_details: bool | None = None
    include_failed_tests: bool | None = None
    fidelity: str | None = None
    allow_scene_save: bool | None = None
    coverage: dict[str, Any] | None = None


class RunTestsStartResponse(MCPResponse):
    data: RunTestsStartData | None = None


class TestJobFailure(BaseModel):
    full_name: str | None = None
    state: str | None = None
    message: str | None = None
    stack_trace: str | None = None
    output: str | None = None


class TestJobProgress(BaseModel):
    selected: int | None = None
    started: int | None = None
    completed: int | None = None
    total: int | None = None
    current_test_full_name: str | None = None
    current_test_started_unix_ms: int | None = None
    last_finished_test_full_name: str | None = None
    last_finished_unix_ms: int | None = None
    stuck_suspected: bool | None = None
    editor_is_focused: bool | None = None
    blocked_reason: str | None = None
    failures_so_far: list[TestJobFailure] | None = None
    failures_capped: bool | None = None


class GetTestJobData(BaseModel):
    job_id: str
    status: str
    outcome: str | None = None
    validation_passed: bool | None = None
    transport_success: bool | None = None
    mode: str | None = None
    started_unix_ms: int | None = None
    finished_unix_ms: int | None = None
    last_update_unix_ms: int | None = None
    initialization: dict[str, Any] | None = None
    progress: TestJobProgress | None = None
    error: str | None = None
    result: RunTestsResult | None = None
    fidelity: str | None = None
    allow_scene_save: bool | None = None
    coverage: dict[str, Any] | None = None
    artifacts: dict[str, Any] | None = None


class GetTestJobResponse(MCPResponse):
    data: GetTestJobData | None = None


@mcp_for_unity_tool(
    group="testing",
    description="Starts a Unity test run asynchronously and returns a job_id immediately. Poll with get_test_job for progress.",
    annotations=ToolAnnotations(
        title="Run Tests",
        destructiveHint=True,
    ),
)
async def run_tests(
    ctx: Context,
    mode: Annotated[Literal["EditMode", "PlayMode"],
                    "Unity test mode to run"] = "EditMode",
    test_names: Annotated[list[str] | str,
                          "Full names of specific tests to run"] | None = None,
    group_names: Annotated[list[str] | str,
                           "Same as test_names, except it allows for Regex"] | None = None,
    category_names: Annotated[list[str] | str,
                              "NUnit category names to filter by"] | None = None,
    assembly_names: Annotated[list[str] | str,
                              "Assembly names to filter tests by"] | None = None,
    include_failed_tests: Annotated[bool,
                                    "Include details for failed/skipped tests (default: true)"] = True,
    include_details: Annotated[bool,
                               "Include details for all tests (default: false)"] = False,
    init_timeout: Annotated[int | None,
                            "Deprecated compatibility name for init_timeout_ms. Values are milliseconds; "
                            "60 means 60ms, not 60 seconds."] = None,
    init_timeout_ms: Annotated[int | None,
                               "Initialization timeout in milliseconds (1000-600000). Defaults to 15000 "
                               "for EditMode and 120000 for PlayMode. Use 60000 for 60 seconds."] = None,
    clear_stuck: Annotated[bool,
                           "Clear an orphaned running job instead of starting a run. Use when a job "
                           "was lost to a domain reload and is blocking every subsequent run."] = False,
    minimum_tests: Annotated[int,
                             "Minimum selected and executed test count required for a pass."] = 1,
    expected_tests: Annotated[list[str] | str,
                              "Exact full test names that must appear in the selected-test manifest."] | None = None,
    fail_on_skipped: Annotated[bool,
                               "Treat skipped or inconclusive tests as a non-passing outcome."] = True,
    fidelity: Annotated[Literal["native", "bridge_preserving"],
                        "native preserves Unity's normal Play Mode behavior; bridge_preserving disables domain reload."] = "native",
    allow_scene_save: Annotated[bool,
                                "Explicitly allow Unity to save already-saved dirty scenes before the run."] = False,
) -> RunTestsStartResponse | MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)

    # Runs before both initialization-timeout validation and preflight on purpose: neither is relevant to
    # clearing, and requires_no_tests would reject the very call that exists to clear the
    # orphaned job blocking it.
    if clear_stuck:
        response = await unity_transport.send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "run_tests",
            {"clear_stuck": True},
        )
        if isinstance(response, dict):
            return MCPResponse(**response)
        return MCPResponse(success=False, error=str(response))

    if init_timeout is not None and init_timeout_ms is not None and init_timeout != init_timeout_ms:
        return MCPResponse(
            success=False,
            code="invalid_init_timeout",
            error="invalid_init_timeout",
            message="init_timeout and init_timeout_ms must match when both are supplied.",
            data={"init_timeout": init_timeout, "init_timeout_ms": init_timeout_ms},
        )
    requested_init_timeout_ms = init_timeout_ms if init_timeout_ms is not None else init_timeout
    effective_init_timeout_ms = (
        requested_init_timeout_ms
        if requested_init_timeout_ms is not None
        else (
            DEFAULT_PLAYMODE_INIT_TIMEOUT_MS
            if mode == "PlayMode"
            else DEFAULT_EDITMODE_INIT_TIMEOUT_MS
        )
    )
    if not MIN_INIT_TIMEOUT_MS <= effective_init_timeout_ms <= MAX_INIT_TIMEOUT_MS:
        return MCPResponse(
            success=False,
            code="invalid_init_timeout",
            error="invalid_init_timeout",
            message=(
                f"Initialization timeout must be {MIN_INIT_TIMEOUT_MS}-{MAX_INIT_TIMEOUT_MS} milliseconds. "
                "Use 60000 for 60 seconds or omit it for the mode-specific default."
            ),
            data={
                "received_ms": requested_init_timeout_ms,
                "effective_ms": effective_init_timeout_ms,
                "minimum_ms": MIN_INIT_TIMEOUT_MS,
                "maximum_ms": MAX_INIT_TIMEOUT_MS,
                "editmode_default_ms": DEFAULT_EDITMODE_INIT_TIMEOUT_MS,
                "playmode_default_ms": DEFAULT_PLAYMODE_INIT_TIMEOUT_MS,
            },
        )
    if minimum_tests < 1:
        return MCPResponse(success=False, error="minimum_tests must be at least 1")

    gate = await preflight(
        ctx,
        requires_no_tests=True,
        wait_for_no_compile=True,
        block_if_dirty=True,
    )
    if isinstance(gate, MCPResponse):
        return gate

    def _coerce_string_list(value) -> list[str] | None:
        if value is None:
            return None
        if isinstance(value, str):
            return [value] if value.strip() else None
        if isinstance(value, list):
            result = [str(v).strip() for v in value if v and str(v).strip()]
            return result if result else None
        return None

    params: dict[str, Any] = {
        "mode": mode,
        "includeFailedTests": include_failed_tests,
        "includeDetails": include_details,
        "minimumTests": minimum_tests,
        "failOnSkipped": fail_on_skipped,
        "fidelity": fidelity,
        "allowSceneSave": allow_scene_save,
        "initTimeout": effective_init_timeout_ms,
    }
    if (t := _coerce_string_list(test_names)):
        params["testNames"] = t
    if (g := _coerce_string_list(group_names)):
        params["groupNames"] = g
    if (c := _coerce_string_list(category_names)):
        params["categoryNames"] = c
    if (a := _coerce_string_list(assembly_names)):
        params["assemblyNames"] = a
    if (expected := _coerce_string_list(expected_tests)):
        params["expectedTests"] = expected
    response = await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "run_tests",
        params,
    )

    if isinstance(response, dict):
        if not response.get("success", True):
            return MCPResponse(**response)
        return RunTestsStartResponse(**response)
    return MCPResponse(success=False, error=str(response))


@mcp_for_unity_tool(
    group="testing",
    description="Polls an async Unity test job by job_id.",
    annotations=ToolAnnotations(
        title="Get Test Job",
        readOnlyHint=True,
    ),
)
async def get_test_job(
    ctx: Context,
    job_id: Annotated[str, "Job id returned by run_tests"],
    include_failed_tests: Annotated[bool,
                                    "Include details for failed/skipped tests (default: true)"] = True,
    include_details: Annotated[bool,
                               "Include details for all tests (default: false)"] = False,
    wait_timeout: Annotated[int | None,
                            "Deprecated compatibility name for wait_timeout_seconds. If set, wait up to "
                            "this many seconds for tests to complete before returning. "
                            "Reduces polling frequency and avoids client-side loop detection. "
                            "Recommended: 30-60 seconds. Returns immediately if tests complete sooner."] = None,
    wait_timeout_seconds: Annotated[int | None,
                                    "If set, wait up to this many seconds for tests to complete before "
                                    "returning. Recommended: 30-60 seconds."] = None,
    allow_focus_nudge: Annotated[bool,
                                 "Allow OS focus changes when an unfocused Editor appears stalled."] = False,
) -> GetTestJobResponse | MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)

    if (
        wait_timeout is not None
        and wait_timeout_seconds is not None
        and wait_timeout != wait_timeout_seconds
    ):
        return MCPResponse(
            success=False,
            code="invalid_wait_timeout",
            error="invalid_wait_timeout",
            message="wait_timeout and wait_timeout_seconds must match when both are supplied.",
            data={"wait_timeout": wait_timeout, "wait_timeout_seconds": wait_timeout_seconds},
        )
    effective_wait_timeout_seconds = (
        wait_timeout_seconds if wait_timeout_seconds is not None else wait_timeout
    )
    if effective_wait_timeout_seconds is not None and effective_wait_timeout_seconds <= 0:
        return MCPResponse(
            success=False,
            code="invalid_wait_timeout",
            error="invalid_wait_timeout",
            message="wait_timeout_seconds must be a positive integer or None.",
            data={"received_seconds": effective_wait_timeout_seconds},
        )

    params: dict[str, Any] = {"job_id": job_id}
    params["includeFailedTests"] = include_failed_tests
    params["includeDetails"] = include_details

    async def _fetch_status() -> dict[str, Any]:
        return await unity_transport.send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "get_test_job",
            params,
        )

    # If wait_timeout is specified, poll server-side until complete or timeout
    if effective_wait_timeout_seconds:
        deadline = asyncio.get_event_loop().time() + effective_wait_timeout_seconds
        poll_interval = 2.0  # Poll Unity every 2 seconds
        prev_last_update_unix_ms = None

        project_path = None

        while True:
            response = await _fetch_status()

            if not isinstance(response, dict):
                return MCPResponse(success=False, error=str(response))

            if not response.get("success", True):
                return MCPResponse(**response)

            # Check if tests are done
            data = response.get("data", {})
            status = data.get("status", "")
            if status in {
                "passed", "failed", "blocked", "infrastructure_error", "no_tests",
                "skipped", "aborted", "cancelled",
            }:
                return GetTestJobResponse(**response) if response.get("success", True) else MCPResponse(**response)

            # Detect progress and reset exponential backoff
            last_update_unix_ms = data.get("last_update_unix_ms")
            if prev_last_update_unix_ms is not None and last_update_unix_ms != prev_last_update_unix_ms:
                # Progress detected - reset exponential backoff for next potential stall
                reset_nudge_backoff()
                logger.debug(f"Test job {job_id} made progress - reset nudge backoff")
            prev_last_update_unix_ms = last_update_unix_ms

            # Check if Unity needs a focus nudge to make progress
            # This handles OS-level throttling (e.g., macOS App Nap) that can
            # stall PlayMode tests when Unity is in the background.
            # Uses exponential backoff: 1s, 2s, 4s, 8s, 10s max between nudges.
            progress = data.get("progress") or {}
            editor_is_focused = progress.get("editor_is_focused", True)
            current_time_ms = int(time.time() * 1000)

            if allow_focus_nudge and should_nudge(
                status=status,
                editor_is_focused=editor_is_focused,
                last_update_unix_ms=last_update_unix_ms,
                current_time_ms=current_time_ms,
                # Use default stall_threshold_ms (3s)
            ):
                logger.info(f"Test job {job_id} appears stalled (unfocused Unity), attempting nudge...")
                # Lazily resolve project path if not yet available (registry may have become ready)
                if project_path is None:
                    project_path = await _get_unity_project_path(unity_instance)
                # Pass project path for multi-instance support
                nudged = await nudge_unity_focus(unity_project_path=project_path)
                if nudged:
                    logger.info(f"Test job {job_id} nudge completed")

            # Check timeout
            remaining = deadline - asyncio.get_event_loop().time()
            if remaining <= 0:
                # Timeout reached, return current status
                return GetTestJobResponse(**response)

            # Wait before next poll (but don't exceed remaining time)
            await asyncio.sleep(min(poll_interval, remaining))
    
    # No wait_timeout - return immediately (original behavior)
    response = await _fetch_status()
    if not isinstance(response, dict):
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        return MCPResponse(**response)

    # Fire-and-forget nudge check: even without wait_timeout, clients may poll
    # externally. Check if Unity needs a nudge on every call so stalls get
    # detected regardless of polling style.
    data = response.get("data", {})
    status = data.get("status", "")
    if allow_focus_nudge and status == "running":
        progress = data.get("progress") or {}
        editor_is_focused = progress.get("editor_is_focused", True)
        last_update_unix_ms = data.get("last_update_unix_ms")
        current_time_ms = int(time.time() * 1000)
        if should_nudge(
            status=status,
            editor_is_focused=editor_is_focused,
            last_update_unix_ms=last_update_unix_ms,
            current_time_ms=current_time_ms,
        ):
            logger.info(f"Test job {job_id} appears stalled (unfocused Unity), scheduling background nudge...")
            project_path = await _get_unity_project_path(unity_instance)
            task = asyncio.create_task(nudge_unity_focus(unity_project_path=project_path))
            _background_tasks.add(task)
            task.add_done_callback(_background_tasks.discard)

    return GetTestJobResponse(**response)
