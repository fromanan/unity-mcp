from typing import Any

from fastmcp import Context
from mcp.types import ToolAnnotations
from models.models import MCPResponse

from services.custom_tool_service import (
    CustomToolService,
    get_user_id_from_context,
    resolve_project_id_for_unity_instance,
)
from services.registry import get_registered_tools, mcp_for_unity_tool
from services.tools import get_unity_instance_from_context


@mcp_for_unity_tool(
    name="execute_custom_tool",
    unity_target=None,
    group=None,
    description="Execute a project-scoped custom tool registered by Unity.",
    annotations=ToolAnnotations(
        title="Execute Custom Tool",
        destructiveHint=True,
    ),
)
async def execute_custom_tool(ctx: Context, tool_name: str, parameters: dict[str, Any] | None = None) -> MCPResponse:
    built_in_tool = next(
        (tool for tool in get_registered_tools() if tool.get("name") == tool_name),
        None,
    )
    if built_in_tool is not None:
        group = built_in_tool.get("group")
        activation_guidance = (
            f" Activate the '{group}' tool group first if it is not already active."
            if group
            else ""
        )
        return MCPResponse(
            success=False,
            error="built_in_tool_requires_direct_call",
            message=(
                f"'{tool_name}' is a built-in MCP tool and cannot be dispatched through "
                f"execute_custom_tool.{activation_guidance} Call '{tool_name}' directly so its "
                "typed validation, preflight, parameter normalization, and polling behavior are preserved."
            ),
            data={"tool_name": tool_name, "group": group},
        )

    unity_instance = await get_unity_instance_from_context(ctx)
    if not unity_instance:
        return MCPResponse(
            success=False,
            message="No active Unity instance. Call set_active_instance with Name@hash from mcpforunity://instances.",
        )

    project_id = resolve_project_id_for_unity_instance(unity_instance)
    if project_id is None:
        return MCPResponse(
            success=False,
            message=f"Could not resolve project id for {unity_instance}. Ensure Unity is running and reachable.",
        )

    # The signature accepts None (parameter-less custom tools). Treat it as an empty
    # dict rather than rejecting — the previous behavior contradicted the optional type.
    if parameters is None:
        parameters = {}
    elif not isinstance(parameters, dict):
        return MCPResponse(
            success=False,
            message="parameters must be an object/dictionary",
        )

    service = CustomToolService.get_instance()
    user_id = await get_user_id_from_context(ctx)
    return await service.execute_tool(
        project_id,
        tool_name,
        unity_instance,
        parameters,
        user_id=user_id,
    )
