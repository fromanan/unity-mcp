from __future__ import annotations

import asyncio
import os
import time
from typing import Any

from models import MCPResponse


def _in_pytest() -> bool:
    # Integration tests in this repo stub transports and do not run against a live Unity editor.
    # Preflight must be a no-op in that environment to avoid breaking the existing test suite.
    return bool(os.environ.get("PYTEST_CURRENT_TEST"))


def _busy(reason: str, retry_after_ms: int) -> MCPResponse:
    return MCPResponse(
        success=False,
        error="busy",
        message=reason,
        hint="retry",
        data={
            "reason": reason,
            "retry_after_ms": int(retry_after_ms),
            "outcome": "blocked",
            "validation_passed": False,
        },
    )


def _infrastructure_error(reason: str, retry_after_ms: int = 500) -> MCPResponse:
    return MCPResponse(
        success=False,
        error="infrastructure_error",
        message=reason,
        hint="retry",
        data={
            "reason": reason,
            "retry_after_ms": int(retry_after_ms),
            "outcome": "infrastructure_error",
            "validation_passed": False,
        },
    )


async def preflight(
    ctx,
    *,
    requires_no_tests: bool = False,
    wait_for_no_compile: bool = False,
    refresh_if_dirty: bool = False,
    max_wait_s: float = 30.0,
) -> MCPResponse | None:
    """
    Server-side preflight guard used by tools so they behave safely even if the client never reads resources.

    Returns:
      - MCPResponse busy/retry payload when the tool should not proceed right now
      - None when the tool should proceed normally
    """
    if _in_pytest():
        return None

    # Load canonical editor state (server enriches advice + staleness).
    try:
        from services.resources.editor_state import get_editor_state
        state_resp = await get_editor_state(ctx)
        state = state_resp.model_dump() if hasattr(
            state_resp, "model_dump") else state_resp
    except Exception:
        return _infrastructure_error("editor_state_unavailable")

    if not isinstance(state, dict) or not state.get("success", False):
        return _infrastructure_error("editor_state_unavailable")

    data = state.get("data")
    if not isinstance(data, dict):
        return _infrastructure_error("invalid_editor_state")

    # Optional refresh-if-dirty
    if refresh_if_dirty:
        assets = data.get("assets")
        if isinstance(assets, dict) and assets.get("external_changes_dirty") is True:
            try:
                from services.tools.refresh_unity import refresh_unity
                await refresh_unity(ctx, mode="if_dirty", scope="all", compile="request", wait_for_ready=True)
            except Exception:
                return _infrastructure_error("asset_refresh_failed", 1000)

    # Tests running: fail fast for tools that require exclusivity.
    if requires_no_tests:
        tests = data.get("tests")
        if isinstance(tests, dict) and tests.get("is_running") is True:
            return _busy("tests_running", 5000)

    # Compilation: optionally wait for a bounded time.
    if wait_for_no_compile:
        deadline = time.monotonic() + float(max_wait_s)
        while True:
            compilation = data.get("compilation") if isinstance(
                data, dict) else None
            is_compiling = isinstance(compilation, dict) and compilation.get(
                "is_compiling") is True
            is_domain_reload_pending = isinstance(compilation, dict) and compilation.get(
                "is_domain_reload_pending") is True
            if not is_compiling and not is_domain_reload_pending:
                break
            if time.monotonic() >= deadline:
                return _busy("compiling", 500)
            await asyncio.sleep(0.25)

            # Refresh state for the next loop iteration.
            try:
                from services.resources.editor_state import get_editor_state
                state_resp = await get_editor_state(ctx)
                state = state_resp.model_dump() if hasattr(
                    state_resp, "model_dump") else state_resp
                data = state.get("data") if isinstance(state, dict) else None
                if not isinstance(data, dict):
                    return _infrastructure_error("invalid_editor_state")
            except Exception:
                return _infrastructure_error("editor_state_unavailable")

    advice = data.get("advice")
    if isinstance(advice, dict):
        blocking_reasons = advice.get("blocking_reasons")
        if isinstance(blocking_reasons, list) and "stale_status" in blocking_reasons:
            return _infrastructure_error("stale_editor_state")

    return None
