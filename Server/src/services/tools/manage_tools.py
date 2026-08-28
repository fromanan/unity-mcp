"""
manage_tools - server-only meta-tool for dynamic tool group activation.

This tool lets the AI assistant (or user) discover available tool groups
and selectively enable / disable them for the current session. Activating
a group makes its tools appear in tool listings; deactivating hides them.

Works on all transports (stdio, HTTP, SSE) via FastMCP 3.x native
per-session visibility.
"""
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import (
    mcp_for_unity_tool,
    TOOL_GROUPS,
    DEFAULT_ENABLED_GROUPS,
    get_group_tool_names,
)


@mcp_for_unity_tool(
    unity_target=None,
    group=None,
    description=(
        "Search and toggle per-session tool groups. Actions: list_groups, search, "
        "activate, deactivate, sync, reset. Search first and activate only the "
        "group needed; sync imports Unity Editor toggle state."
    ),
    annotations=ToolAnnotations(
        title="Manage Tools",
        readOnlyHint=False,
    ),
)
async def manage_tools(
    ctx: Context,
    action: Annotated[
        Literal["list_groups", "search", "activate", "deactivate", "sync", "reset"],
        "Action to perform."
    ],
    group: Annotated[
        str | None,
        "Group name for activate or deactivate."
    ] = None,
    query: Annotated[
        str | None,
        "Search text for action=search. Matches group names, descriptions, and tool names.",
    ] = None,
    include_tools: Annotated[
        bool,
        "Include tool names in list_groups. Defaults false for a compact response.",
    ] = False,
) -> dict[str, Any]:
    if action == "list_groups":
        return await _list_groups(ctx, include_tools=include_tools)

    if action == "search":
        if not query or not query.strip():
            return {"error": "query is required for search"}
        return _search_tools(query)

    if action in ("activate", "deactivate"):
        if not group:
            return {"error": f"group is required for {action}"}
        group = group.strip().lower()
        if group not in TOOL_GROUPS:
            return {"error": f"Unknown group '{group}'. Valid: {', '.join(sorted(TOOL_GROUPS))}"}

    if action == "activate":
        if await _is_group_enabled(ctx, group):
            return {
                "activated": group,
                "unchanged": True,
                "tool_count": len(get_group_tool_names().get(group, [])),
            }
        tag = f"group:{group}"
        await ctx.enable_components(tags={tag}, components={"tool"})
        return {
            "activated": group,
            "tool_count": len(get_group_tool_names().get(group, [])),
            "message": f"Group '{group}' is now visible. Its tools will appear in tool listings.",
        }

    if action == "deactivate":
        if not await _is_group_enabled(ctx, group):
            return {
                "deactivated": group,
                "unchanged": True,
                "tool_count": len(get_group_tool_names().get(group, [])),
            }
        tag = f"group:{group}"
        await ctx.disable_components(tags={tag}, components={"tool"})
        return {
            "deactivated": group,
            "tool_count": len(get_group_tool_names().get(group, [])),
            "message": f"Group '{group}' is now hidden.",
        }

    if action == "sync":
        from services.tools import sync_tool_visibility_from_unity
        result = await sync_tool_visibility_from_unity(notify=True)
        if result.get("error"):
            msg = result["error"]
            if result.get("unsupported"):
                msg = (
                    "The connected Unity Editor does not support tool state syncing yet. "
                    "Update the MCPForUnity package to the latest version, then try again. "
                    "In the meantime, use activate/deactivate actions to toggle groups manually."
                )
            else:
                msg = f"Failed to sync tool visibility from Unity. Is Unity running? ({msg})"
            return {"error": msg}
        return {
            "synced": True,
            "enabled_groups": result.get("enabled_groups", []),
            "disabled_groups": result.get("disabled_groups", []),
            "enabled_tool_count": result.get("enabled_tool_count", 0),
            "total_tool_count": result.get("total_tool_count", 0),
            "message": (
                "Tool visibility synced from Unity Editor. "
                f"Enabled groups: {', '.join(result.get('enabled_groups', []))}. "
                f"Disabled groups: {', '.join(result.get('disabled_groups', []) or ['none'])}."
            ),
        }

    if action == "reset":
        await ctx.reset_visibility()
        return {
            "reset": True,
            "default_groups": sorted(DEFAULT_ENABLED_GROUPS),
            "message": "Tool visibility restored to server defaults.",
        }

    return {"error": f"Unknown action '{action}'"}


async def _list_groups(ctx: Context, *, include_tools: bool = False) -> dict[str, Any]:
    """Build the list_groups response with group metadata and tool names."""
    group_tools = get_group_tool_names()
    session_enabled = await _session_group_overrides(ctx)

    groups = []
    for name in sorted(TOOL_GROUPS.keys()):
        if name in session_enabled:
            currently_enabled = session_enabled[name]
        else:
            currently_enabled = name in DEFAULT_ENABLED_GROUPS
        item = {
            "name": name,
            "description": TOOL_GROUPS[name],
            "enabled": currently_enabled,
            "default_enabled": name in DEFAULT_ENABLED_GROUPS,
            "tool_count": len(group_tools.get(name, [])),
        }
        if include_tools:
            item["tools"] = group_tools.get(name, [])
        groups.append(item)
    return {
        "groups": groups,
        "note": (
            "Use activate/deactivate to toggle groups for this session. "
            "Tools with group=None (server meta-tools) are always visible."
        ),
    }


async def _is_group_enabled(ctx: Context, group_name: str) -> bool:
    overrides = await _session_group_overrides(ctx)
    return overrides.get(group_name, group_name in DEFAULT_ENABLED_GROUPS)


async def _session_group_overrides(ctx: Context) -> dict[str, bool]:
    """Collapse accumulated visibility rules to their latest group states."""
    session_enabled: dict[str, bool] = {}
    try:
        rules = await ctx._get_visibility_rules()
        for rule in rules:
            tags = rule.get("tags") or []
            enabled = rule.get("enabled", True)
            for tag in tags:
                if isinstance(tag, str) and tag.startswith("group:"):
                    session_enabled[tag[len("group:"):]] = enabled
    except Exception:
        pass
    return session_enabled


def _search_tools(query: str) -> dict[str, Any]:
    needle = query.strip().lower()
    matches: list[dict[str, Any]] = []
    for group_name, tool_names in get_group_tool_names().items():
        group_description = TOOL_GROUPS[group_name]
        matching_tools = [name for name in tool_names if needle in name.lower()]
        if (
            needle in group_name.lower()
            or needle in group_description.lower()
            or matching_tools
        ):
            matches.append({
                "group": group_name,
                "description": group_description,
                "matching_tools": matching_tools[:12],
                "matching_tool_count": len(matching_tools),
            })
    return {
        "query": query.strip(),
        "matches": matches[:8],
        "match_count": len(matches),
        "message": "Activate a matching group to expose its tools.",
    }
