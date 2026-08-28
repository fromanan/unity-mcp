"""Paged resource access for oversized tool results."""

from __future__ import annotations

from typing import Annotated, Any

from fastmcp import Context

from core.result_budget import result_owner_from_context
from services.registry import mcp_for_unity_resource
from services.state.result_store import result_store

RESULT_CHUNK_CHARS = 16_000


@mcp_for_unity_resource(
    uri="mcpforunity://results/{result_id}/{offset}",
    name="stored_result",
    description=(
        "Read one bounded page of an oversized tool result. Use the exact result_uri "
        "returned by the tool and follow next_uri until null."
    ),
)
async def get_stored_result(
    ctx: Context,
    result_id: Annotated[str, "Opaque result id from a tool's result_uri."],
    offset: Annotated[int, "Character offset from the exact current or next_uri."],
) -> dict[str, Any]:
    owner = await result_owner_from_context(ctx)
    entry = result_store.get(result_id, owner=owner)
    if entry is None:
        return {
            "success": False,
            "message": "Stored result was not found, expired, or belongs to another session.",
        }

    start = max(0, int(offset))
    end = min(len(entry.content), start + RESULT_CHUNK_CHARS)
    next_uri = (
        f"mcpforunity://results/{result_id}/{end}"
        if end < len(entry.content)
        else None
    )
    return {
        "success": True,
        "data": {
            "tool": entry.tool_name,
            "content_type": entry.content_type,
            "offset": start,
            "next_offset": end,
            "total_chars": len(entry.content),
            "total_bytes": entry.size_bytes,
            "content": entry.content[start:end],
            "next_uri": next_uri,
        },
    }
