"""Low-overhead telemetry decorators for MCP tools and resources."""

from __future__ import annotations

import functools
import inspect
import logging
import time
from typing import Any, Callable

from core.logging_decorator import bounded_diagnostic_text
from core.telemetry import (
    MilestoneType,
    record_milestone,
    record_resource_usage,
    record_tool_usage,
)

_log = logging.getLogger("unity-mcp-telemetry")
_MAX_ERROR_CHARS = 256


def _bounded_error(exc: Exception) -> str:
    return bounded_diagnostic_text(exc, _MAX_ERROR_CHARS)


def telemetry_tool(tool_name: str):
    """Decorator to add telemetry tracking to MCP tools."""
    def decorator(func: Callable) -> Callable:
        action_arg_index: int | None = None
        try:
            signature = inspect.signature(func)
            positional_parameters = (
                parameter
                for parameter in signature.parameters.values()
                if parameter.kind in (
                    inspect.Parameter.POSITIONAL_ONLY,
                    inspect.Parameter.POSITIONAL_OR_KEYWORD,
                )
            )
            action_arg_index = next(
                (
                    index
                    for index, parameter in enumerate(positional_parameters)
                    if parameter.name == "action"
                ),
                None,
            )
        except (TypeError, ValueError):
            pass

        def extract_sub_action(args: tuple[Any, ...], kwargs: dict[str, Any]) -> Any:
            if "action" in kwargs:
                return kwargs["action"]
            if action_arg_index is not None and action_arg_index < len(args):
                return args[action_arg_index]
            return None

        def emit_milestones(action: Any) -> None:
            try:
                if tool_name == "manage_script" and action == "create":
                    record_milestone(MilestoneType.FIRST_SCRIPT_CREATION)
                elif tool_name.startswith("manage_scene"):
                    record_milestone(MilestoneType.FIRST_SCENE_MODIFICATION)
                record_milestone(MilestoneType.FIRST_TOOL_USAGE)
            except Exception:
                _log.debug("milestone emit failed", exc_info=True)

        @functools.wraps(func)
        def _sync_wrapper(*args, **kwargs) -> Any:
            started = time.perf_counter()
            success = False
            error = None
            sub_action = extract_sub_action(args, kwargs)
            try:
                result = func(*args, **kwargs)
                success = True
                emit_milestones(sub_action)
                return result
            except Exception as exc:
                error = _bounded_error(exc)
                raise
            finally:
                try:
                    record_tool_usage(
                        tool_name,
                        success,
                        (time.perf_counter() - started) * 1000,
                        error,
                        sub_action=sub_action,
                    )
                except Exception:
                    _log.debug("record_tool_usage failed", exc_info=True)

        @functools.wraps(func)
        async def _async_wrapper(*args, **kwargs) -> Any:
            started = time.perf_counter()
            success = False
            error = None
            sub_action = extract_sub_action(args, kwargs)
            try:
                result = await func(*args, **kwargs)
                success = True
                emit_milestones(sub_action)
                return result
            except Exception as exc:
                error = _bounded_error(exc)
                raise
            finally:
                try:
                    record_tool_usage(
                        tool_name,
                        success,
                        (time.perf_counter() - started) * 1000,
                        error,
                        sub_action=sub_action,
                    )
                except Exception:
                    _log.debug("record_tool_usage failed", exc_info=True)

        return _async_wrapper if inspect.iscoroutinefunction(func) else _sync_wrapper
    return decorator


def telemetry_resource(resource_name: str):
    """Decorator to add telemetry tracking to MCP resources."""
    def decorator(func: Callable) -> Callable:
        @functools.wraps(func)
        def _sync_wrapper(*args, **kwargs) -> Any:
            started = time.perf_counter()
            success = False
            error = None
            try:
                result = func(*args, **kwargs)
                success = True
                return result
            except Exception as exc:
                error = _bounded_error(exc)
                raise
            finally:
                try:
                    record_resource_usage(
                        resource_name,
                        success,
                        (time.perf_counter() - started) * 1000,
                        error,
                    )
                except Exception:
                    _log.debug("record_resource_usage failed", exc_info=True)

        @functools.wraps(func)
        async def _async_wrapper(*args, **kwargs) -> Any:
            started = time.perf_counter()
            success = False
            error = None
            try:
                result = await func(*args, **kwargs)
                success = True
                return result
            except Exception as exc:
                error = _bounded_error(exc)
                raise
            finally:
                try:
                    record_resource_usage(
                        resource_name,
                        success,
                        (time.perf_counter() - started) * 1000,
                        error,
                    )
                except Exception:
                    _log.debug("record_resource_usage failed", exc_info=True)

        return _async_wrapper if inspect.iscoroutinefunction(func) else _sync_wrapper
    return decorator
