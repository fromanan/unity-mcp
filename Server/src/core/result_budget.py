"""Automatic inline-result budgets with paged result-resource fallback."""

from __future__ import annotations

import functools
import inspect
import json
from json.encoder import encode_basestring
import os
from dataclasses import dataclass
from typing import Any, Callable

from pydantic import BaseModel

from core.logging_decorator import summarize_value
from models.models import MCPResponse
from services.state.result_store import result_store

DEFAULT_INLINE_RESULT_BYTES = 256 * 1024
_SIZE_SCAN_CHARS = 64 * 1024


@dataclass(frozen=True, slots=True)
class _SerializedResult:
    content: str | None
    content_type: str
    size_bytes: int
    complete: bool


def _configured_inline_limit() -> int:
    try:
        configured = int(os.environ.get(
            "UNITY_MCP_INLINE_RESULT_MAX_BYTES",
            str(DEFAULT_INLINE_RESULT_BYTES),
        ))
    except ValueError:
        configured = DEFAULT_INLINE_RESULT_BYTES
    return min(max(configured, 16 * 1024), 4 * 1024 * 1024)


INLINE_RESULT_MAX_BYTES = _configured_inline_limit()


async def result_owner_from_context(ctx: Any) -> str:
    session_id = getattr(ctx, "session_id", None)
    user_id = None
    get_state = getattr(ctx, "get_state", None)
    if callable(get_state):
        try:
            user_id = await get_state("user_id")
        except Exception:
            user_id = None
    return f"{user_id or 'local'}:{session_id or 'session'}"


def _find_context(args: tuple[Any, ...], kwargs: dict[str, Any]) -> Any:
    if "ctx" in kwargs:
        return kwargs["ctx"]
    for candidate in args:
        if hasattr(candidate, "session_id") or callable(
            getattr(candidate, "get_state", None)
        ):
            return candidate
    return None


def _bounded_utf8_size(value: str, ceiling: int) -> int:
    total = 0
    for start in range(0, len(value), _SIZE_SCAN_CHARS):
        total += len(value[start:start + _SIZE_SCAN_CHARS].encode("utf-8"))
        if total > ceiling:
            return ceiling + 1
    return total


def _bounded_json_string_size(value: str, ceiling: int) -> int:
    """Return the exact UTF-8 JSON-string size, stopping above ``ceiling``."""
    total = 2  # Opening and closing quotes.
    for start in range(0, len(value), _SIZE_SCAN_CHARS):
        encoded = encode_basestring(value[start:start + _SIZE_SCAN_CHARS])
        total += len(encoded[1:-1].encode("utf-8"))
        if total > ceiling:
            return ceiling + 1
    return total


def _minimum_json_bytes(
    value: Any,
    ceiling: int,
    *,
    seen: set[int] | None = None,
    depth: int = 0,
) -> int:
    """Return a bounded lower bound before allocating a serialized payload."""
    if depth > 100:
        return ceiling + 1
    if value is None:
        return 4
    if value is True:
        return 4
    if value is False:
        return 5
    if isinstance(value, (int, float)):
        return min(
            ceiling + 1,
            len(json.dumps(value, ensure_ascii=False).encode("utf-8")),
        )
    if isinstance(value, str):
        return _bounded_json_string_size(value, ceiling)
    if isinstance(value, (bytes, bytearray, memoryview)):
        return _bounded_json_string_size(str(value), ceiling)

    if seen is None:
        seen = set()
    if isinstance(value, BaseModel):
        if isinstance(value, MCPResponse):
            value = {
                "success": value.success,
                "message": value.message,
                "error": value.error,
                "data": value.data,
                "hint": value.hint,
            }
            value = {key: item for key, item in value.items() if item is not None}
        else:
            value = value.model_dump(mode="json", exclude_none=True)

    identity = id(value)
    if identity in seen:
        return ceiling + 1

    if isinstance(value, dict):
        seen.add(identity)
        total = 2
        try:
            for index, (key, item) in enumerate(value.items()):
                if index:
                    total += 1
                if isinstance(key, str):
                    key_text = key
                elif key is None:
                    key_text = "null"
                elif key is True:
                    key_text = "true"
                elif key is False:
                    key_text = "false"
                elif isinstance(key, (int, float)):
                    key_text = json.dumps(key, ensure_ascii=False)
                else:
                    return ceiling + 1
                total += _bounded_json_string_size(
                    key_text,
                    max(0, ceiling - total),
                ) + 1
                if total > ceiling:
                    return ceiling + 1
                total += _minimum_json_bytes(
                    item,
                    max(0, ceiling - total),
                    seen=seen,
                    depth=depth + 1,
                )
                if total > ceiling:
                    return ceiling + 1
            return total
        finally:
            seen.discard(identity)

    if isinstance(value, (list, tuple)):
        seen.add(identity)
        total = 2
        try:
            for index, item in enumerate(value):
                if index:
                    total += 1
                total += _minimum_json_bytes(
                    item,
                    max(0, ceiling - total),
                    seen=seen,
                    depth=depth + 1,
                )
                if total > ceiling:
                    return ceiling + 1
            return total
        finally:
            seen.discard(identity)

    return _bounded_json_string_size(str(value), ceiling)


def bounded_json_size(value: Any, *, ceiling: int) -> tuple[int, bool]:
    """Size JSON-native data without constructing a full serialized copy.

    The returned size is exact when ``within_limit`` is true. Once the scan
    crosses ``ceiling``, it stops and returns a bounded over-limit sentinel.
    """
    normalized_ceiling = max(0, int(ceiling))
    size_bytes = _minimum_json_bytes(value, normalized_ceiling)
    return size_bytes, size_bytes <= normalized_ceiling


def _json_payload(result: Any) -> Any:
    if isinstance(result, MCPResponse):
        payload = {
            "success": result.success,
            "message": result.message,
            "error": result.error,
            "data": result.data,
            "hint": result.hint,
        }
        return {key: value for key, value in payload.items() if value is not None}
    if isinstance(result, BaseModel):
        return result.model_dump(mode="json", exclude_none=True)
    return result


def _serialize_result(
    result: Any,
    *,
    inline_ceiling: int,
    store_ceiling: int,
) -> _SerializedResult | None:
    if isinstance(result, str):
        inline_size = _bounded_utf8_size(result, inline_ceiling)
        if inline_size <= inline_ceiling:
            return _SerializedResult(
                content=None,
                content_type="text/plain",
                size_bytes=inline_size,
                complete=True,
            )
        size_bytes = _bounded_utf8_size(result, store_ceiling)
        complete = size_bytes <= store_ceiling
        return _SerializedResult(
            content=result if complete else None,
            content_type="text/plain",
            size_bytes=size_bytes,
            complete=complete,
        )
    if isinstance(result, (BaseModel, dict, list)):
        payload = _json_payload(result)
        inline_size = _minimum_json_bytes(payload, inline_ceiling)
        if inline_size <= inline_ceiling:
            return _SerializedResult(
                content=None,
                content_type="application/json",
                size_bytes=inline_size,
                complete=True,
            )
        minimum_bytes = _minimum_json_bytes(payload, store_ceiling)
        if minimum_bytes > store_ceiling:
            return _SerializedResult(
                content=None,
                content_type="application/json",
                size_bytes=minimum_bytes,
                complete=False,
            )
        content = json.dumps(
            payload,
            ensure_ascii=False,
            separators=(",", ":"),
            default=str,
        )
        size_bytes = _bounded_utf8_size(content, store_ceiling)
        return _SerializedResult(
            content=content if size_bytes <= store_ceiling else None,
            content_type="application/json",
            size_bytes=size_bytes,
            complete=size_bytes <= store_ceiling,
        )
    return None


def _truncated_result(
    result: Any,
    *,
    result_id: str | None,
    size_bytes: int,
    size_is_exact: bool,
) -> Any:
    pointer = (
        f"mcpforunity://results/{result_id}/0" if result_id is not None else None
    )
    payload = {
        "truncated": True,
        "summary": summarize_value(result),
        "result_uri": pointer,
        "stored": result_id is not None,
    }
    if size_is_exact:
        payload["original_bytes"] = size_bytes
    else:
        payload["minimum_bytes"] = size_bytes
    message = (
        "Result exceeded the inline payload budget. Read the paged result_uri."
        if result_id is not None
        else "Result exceeded both inline and stored-result limits. Narrow or page the request."
    )

    if isinstance(result, MCPResponse):
        original_message = result.message
        return result.model_copy(update={
            "message": f"{original_message} {message}".strip(),
            "data": payload,
        })
    if isinstance(result, BaseModel):
        return MCPResponse(success=True, message=message, data=payload)
    if isinstance(result, dict):
        original_message = result.get("message")
        response = {
            "success": result.get("success", True),
            "message": f"{original_message or ''} {message}".strip(),
            "data": payload,
        }
        for key in ("error", "hint"):
            if result.get(key) is not None:
                response[key] = result[key]
        return response
    if isinstance(result, str):
        return json.dumps({"message": message, **payload}, separators=(",", ":"))
    return result


async def apply_result_budget(result: Any, *, tool_name: str, ctx: Any) -> Any:
    inline_ceiling = min(INLINE_RESULT_MAX_BYTES, result_store.max_item_bytes)
    serialized = _serialize_result(
        result,
        inline_ceiling=inline_ceiling,
        store_ceiling=result_store.max_item_bytes,
    )
    if serialized is None:
        return result
    if serialized.complete and serialized.size_bytes <= inline_ceiling:
        return result

    owner = await result_owner_from_context(ctx)
    result_id = None
    if serialized.content is not None:
        result_id = result_store.put(
            serialized.content,
            content_type=serialized.content_type,
            owner=owner,
            tool_name=tool_name,
            size_bytes=serialized.size_bytes,
        )
    return _truncated_result(
        result,
        result_id=result_id,
        size_bytes=serialized.size_bytes,
        size_is_exact=serialized.complete,
    )


def enforce_result_budget(tool_name: str):
    """Decorate tool functions so pathological payloads become paged resources."""
    def decorator(func: Callable) -> Callable:
        @functools.wraps(func)
        async def _async_wrapper(*args, **kwargs) -> Any:
            result = await func(*args, **kwargs)
            return await apply_result_budget(
                result,
                tool_name=tool_name,
                ctx=_find_context(args, kwargs),
            )

        @functools.wraps(func)
        def _sync_wrapper(*args, **kwargs) -> Any:
            # Sync tools cannot safely await session state. They retain their
            # existing result contract; all current Unity MCP tools are async.
            return func(*args, **kwargs)

        return _async_wrapper if inspect.iscoroutinefunction(func) else _sync_wrapper
    return decorator
