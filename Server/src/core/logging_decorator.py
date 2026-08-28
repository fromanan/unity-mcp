"""Low-overhead, content-safe execution logging for MCP components."""

from __future__ import annotations

from collections.abc import Mapping, Sequence
import functools
import inspect
from itertools import islice
import logging
import re
import time
from typing import Any, Callable

logger = logging.getLogger("mcp-for-unity-server")

_MAX_KEYS = 12
_MAX_TEXT_PREVIEW = 160
_SECRET_PATTERN = re.compile(
    r"(?i)(api[_-]?key|authorization|bearer|token|secret|password)(\s*[:=]\s*)([^\s,;]+)"
)


def bounded_diagnostic_text(value: object, limit: int = _MAX_TEXT_PREVIEW) -> str:
    """Return a redacted, single-line diagnostic string with a hard bound."""
    text = str(value).replace("\r", "\\r").replace("\n", "\\n")
    text = _SECRET_PATTERN.sub(r"\1\2<redacted>", text)
    if len(text) <= limit:
        return text
    return f"{text[:limit]}...(+{len(text) - limit} chars)"


def summarize_value(value: Any) -> object:
    """Summarize shape and size without logging user payload contents."""
    if value is None or isinstance(value, (bool, int, float)):
        return value
    if isinstance(value, str):
        return {"type": "str", "chars": len(value)}
    if isinstance(value, (bytes, bytearray, memoryview)):
        return {"type": type(value).__name__, "bytes": len(value)}
    if isinstance(value, Mapping):
        keys = [str(key) for key in islice(value.keys(), _MAX_KEYS)]
        summary: dict[str, object] = {
            "type": type(value).__name__,
            "items": len(value),
            "keys": keys,
        }
        if len(value) > _MAX_KEYS:
            summary["omitted_keys"] = len(value) - _MAX_KEYS
        return summary
    if isinstance(value, Sequence):
        return {"type": type(value).__name__, "items": len(value)}
    return {"type": type(value).__name__}


def _summarize_call(args: tuple[Any, ...], kwargs: dict[str, Any]) -> dict[str, object]:
    return {
        "positional": [summarize_value(value) for value in args[:_MAX_KEYS]],
        "keyword": {
            str(key): summarize_value(value)
            for key, value in islice(kwargs.items(), _MAX_KEYS)
        },
        "omitted": max(0, len(args) - _MAX_KEYS) + max(0, len(kwargs) - _MAX_KEYS),
    }


def log_execution(name: str, type_label: str):
    """Log execution metadata while keeping payloads and results out of logs."""
    def decorator(func: Callable) -> Callable:
        @functools.wraps(func)
        def _sync_wrapper(*args, **kwargs) -> Any:
            started = time.perf_counter()
            if logger.isEnabledFor(logging.INFO):
                logger.info(
                    "%s '%s' started: %s",
                    type_label,
                    name,
                    _summarize_call(args, kwargs),
                )
            try:
                result = func(*args, **kwargs)
                if logger.isEnabledFor(logging.INFO):
                    logger.info(
                        "%s '%s' completed in %.2fms: %s",
                        type_label,
                        name,
                        (time.perf_counter() - started) * 1000,
                        summarize_value(result),
                    )
                return result
            except Exception as exc:
                logger.warning(
                    "%s '%s' failed in %.2fms (%s): %s",
                    type_label,
                    name,
                    (time.perf_counter() - started) * 1000,
                    type(exc).__name__,
                    bounded_diagnostic_text(exc),
                )
                raise

        @functools.wraps(func)
        async def _async_wrapper(*args, **kwargs) -> Any:
            started = time.perf_counter()
            if logger.isEnabledFor(logging.INFO):
                logger.info(
                    "%s '%s' started: %s",
                    type_label,
                    name,
                    _summarize_call(args, kwargs),
                )
            try:
                result = await func(*args, **kwargs)
                if logger.isEnabledFor(logging.INFO):
                    logger.info(
                        "%s '%s' completed in %.2fms: %s",
                        type_label,
                        name,
                        (time.perf_counter() - started) * 1000,
                        summarize_value(result),
                    )
                return result
            except Exception as exc:
                logger.warning(
                    "%s '%s' failed in %.2fms (%s): %s",
                    type_label,
                    name,
                    (time.perf_counter() - started) * 1000,
                    type(exc).__name__,
                    bounded_diagnostic_text(exc),
                )
                raise

        return _async_wrapper if inspect.iscoroutinefunction(func) else _sync_wrapper
    return decorator
