"""First-class rendering inspection and transactional authoring tools."""

import json
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.refresh_unity import wait_for_editor_ready
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


def _clean_params(values: dict[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in values.items() if value is not None}


def _parse_object(value: dict[str, Any] | list[dict[str, Any]] | str | None) -> Any:
    if isinstance(value, str):
        try:
            return json.loads(value)
        except json.JSONDecodeError as exc:
            raise ValueError(f"Invalid JSON payload: {exc.msg}") from exc
    return value


async def _send(
    ctx: Context,
    command: str,
    params: dict[str, Any],
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        command,
        _clean_params(params),
    )
    return response if isinstance(response, dict) else {
        "success": False,
        "message": str(response),
    }


@mcp_for_unity_tool(
    unity_target="inspect_rendering",
    group="rendering_inspect",
    description=(
        "Inspect the actual render-owner closure for a scene object: renderers, "
        "submeshes, material slots, material property blocks, LOD membership, "
        "lightmap state, and package/asset ownership. Results are paged and read-only."
    ),
    annotations=ToolAnnotations(
        title="Inspect Render Target",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def inspect_render_target(
    ctx: Context,
    target: Annotated[str, "GameObject name, hierarchy path, or instance ID."],
    include_children: Annotated[bool, "Inspect child renderers."] = True,
    include_inactive: Annotated[bool, "Include inactive child renderers."] = True,
    page_size: Annotated[int, "Renderer records per page (1-100)."] = 25,
    cursor: Annotated[int, "Zero-based renderer cursor."] = 0,
) -> dict[str, Any]:
    return await _send(ctx, "inspect_rendering", {
        "action": "inspect_render_target",
        "target": target,
        "include_children": include_children,
        "include_inactive": include_inactive,
        "page_size": page_size,
        "cursor": cursor,
    })


@mcp_for_unity_tool(
    unity_target="inspect_rendering",
    group="rendering_inspect",
    description=(
        "Inspect one material by exact asset path, including path/GUID, shader "
        "identity and kind, typed current/default values, texture path/GUID and "
        "tiling, keywords, passes, queue/surface state, GI/instancing/SRP Batcher "
        "evidence, and paged live renderer consumers."
    ),
    annotations=ToolAnnotations(
        title="Inspect Material",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def inspect_material(
    ctx: Context,
    material_path: Annotated[str, "Exact material path under Assets/ or Packages/."],
    include_consumers: Annotated[bool, "Include live scene renderer consumers."] = True,
    page_size: Annotated[int, "Consumer records per page (1-100)."] = 25,
    cursor: Annotated[int, "Zero-based consumer cursor."] = 0,
) -> dict[str, Any]:
    return await _send(ctx, "inspect_rendering", {
        "action": "inspect_material",
        "material_path": material_path,
        "include_consumers": include_consumers,
        "page_size": page_size,
        "cursor": cursor,
    })


@mcp_for_unity_tool(
    unity_target="inspect_rendering",
    group="rendering_inspect",
    description=(
        "Inspect one texture by exact asset path. Reports source/imported size, "
        "importer and platform overrides, runtime format/storage, color-space and "
        "mip settings, bounded per-channel statistics, edge discontinuity, normal "
        "validity, and a project semantic-contract classification."
    ),
    annotations=ToolAnnotations(
        title="Inspect Texture",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def inspect_texture(
    ctx: Context,
    texture_path: Annotated[str, "Exact texture path under Assets/ or Packages/."],
    semantic_contract: Annotated[
        str | None,
        "Optional contract name such as freshcan_n_ao_r, urp_mask, normal, or color.",
    ] = None,
    sample_size: Annotated[int, "Maximum sampled width/height (16-256)."] = 128,
) -> dict[str, Any]:
    return await _send(ctx, "inspect_rendering", {
        "action": "inspect_texture",
        "texture_path": texture_path,
        "semantic_contract": semantic_contract,
        "sample_size": sample_size,
    })


@mcp_for_unity_tool(
    unity_target="inspect_rendering",
    group="rendering_inspect",
    description=(
        "Inspect a ShaderLab, Shader Graph, or Sub Graph asset by exact path. For "
        "graphs, parses concatenated JSON documents without rewriting them and "
        "reports targets, blackboard properties, nodes, slots, edges, subgraphs, "
        "property-to-output reachability, inert properties, passes, keywords, and "
        "compiler messages."
    ),
    annotations=ToolAnnotations(
        title="Inspect Shader Graph",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def inspect_shader_graph(
    ctx: Context,
    shader_path: Annotated[str, "Exact .shader, .shadergraph, or .shadersubgraph path."],
    page_size: Annotated[int, "Graph-document summaries per page (1-100)."] = 50,
    cursor: Annotated[int, "Zero-based graph-document cursor."] = 0,
) -> dict[str, Any]:
    return await _send(ctx, "inspect_rendering", {
        "action": "inspect_shader_graph",
        "shader_path": shader_path,
        "page_size": page_size,
        "cursor": cursor,
    })


@mcp_for_unity_tool(
    unity_target="inspect_rendering",
    group="rendering_inspect",
    description=(
        "Validate the renderer-material-shader-texture closure for an exact material "
        "or scene target. Checks bindings, graph reachability, texture/importer "
        "semantics, LOD variants, ownership/vendor boundaries, and caller-supplied "
        "contracts. Unknown proof fails the strict contract instead of passing green."
    ),
    annotations=ToolAnnotations(
        title="Validate Render Contract",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def validate_render_contract(
    ctx: Context,
    material_path: Annotated[str | None, "Exact material path."] = None,
    target: Annotated[str | None, "Scene GameObject name, path, or instance ID."] = None,
    contracts: Annotated[
        dict[str, Any] | str | None,
        "Optional JSON contract overrides keyed by material property or texture path.",
    ] = None,
    strict: Annotated[bool, "Treat unknown proof as a validation failure."] = True,
) -> dict[str, Any]:
    try:
        parsed_contracts = _parse_object(contracts)
    except ValueError as exc:
        return {"success": False, "message": str(exc)}
    return await _send(ctx, "inspect_rendering", {
        "action": "validate_render_contract",
        "material_path": material_path,
        "target": target,
        "contracts": parsed_contracts,
        "strict": strict,
    })


@mcp_for_unity_tool(
    unity_target="inspect_rendering",
    group="rendering_inspect",
    description=(
        "Capture a deterministic color or wireframe render probe from an existing "
        "camera. Locks width, height, quality, camera state, warmup count, output "
        "path, and restoration evidence; rejects unsupported debug channels."
    ),
    annotations=ToolAnnotations(
        title="Render Probe",
        readOnlyHint=False,
        destructiveHint=False,
    ),
)
async def render_probe(
    ctx: Context,
    camera: Annotated[str | None, "Camera GameObject name, path, or instance ID."] = None,
    target: Annotated[str | None, "Optional target recorded with the capture manifest."] = None,
    scope: Annotated[
        Literal["scene", "target"],
        "Capture the full camera scene or an isolated target preview.",
    ] = "scene",
    output_path: Annotated[
        str | None,
        "Project-relative output path; defaults under Library/MCPForUnity/RenderProbes.",
    ] = None,
    width: Annotated[int, "Capture width (64-4096)."] = 1024,
    height: Annotated[int, "Capture height (64-4096)."] = 1024,
    channel: Annotated[Literal["color", "wireframe"], "Supported capture channel."] = "color",
    warmup_frames: Annotated[int, "Synchronous camera renders before capture (0-8)."] = 1,
    quality_level: Annotated[int | None, "Optional quality-level index, restored afterward."] = None,
) -> dict[str, Any]:
    return await _send(ctx, "inspect_rendering", {
        "action": "render_probe",
        "camera": camera,
        "target": target,
        "scope": scope,
        "output_path": output_path,
        "width": width,
        "height": height,
        "channel": channel,
        "warmup_frames": warmup_frames,
        "quality_level": quality_level,
    })


@mcp_for_unity_tool(
    unity_target="inspect_rendering",
    group="rendering_inspect",
    description=(
        "Profile one scene render target with static renderer/material/pass/mesh "
        "evidence and a paged Frame Debugger snapshot filtered to its renderer "
        "instance IDs when Frame Debugger data is available. Reports proof levels."
    ),
    annotations=ToolAnnotations(
        title="Profile Render Target",
        readOnlyHint=True,
        destructiveHint=False,
    ),
)
async def profile_render_target(
    ctx: Context,
    target: Annotated[str, "GameObject name, hierarchy path, or instance ID."],
    include_frame_debugger: Annotated[bool, "Include currently captured Frame Debugger events."] = True,
    page_size: Annotated[int, "Frame Debugger records per page (1-100)."] = 50,
    cursor: Annotated[int, "Zero-based Frame Debugger cursor."] = 0,
) -> dict[str, Any]:
    return await _send(ctx, "inspect_rendering", {
        "action": "profile_render_target",
        "target": target,
        "include_frame_debugger": include_frame_debugger,
        "page_size": page_size,
        "cursor": cursor,
    })


@mcp_for_unity_tool(
    unity_target="manage_rendering_authoring",
    group="rendering_authoring",
    description=(
        "Plan or apply a transactional material, texture-importer, or Shader Graph "
        "patch. Dry-run is the default. Apply requires an expected SHA-256, uses "
        "typed/structured operations, enforces project-copy/vendor boundaries, "
        "records an exact mutation manifest, imports, waits for editor readiness, "
        "and returns semantic post-validation."
    ),
    annotations=ToolAnnotations(
        title="Manage Rendering Authoring",
        destructiveHint=True,
    ),
)
async def manage_rendering_authoring(
    ctx: Context,
    asset_path: Annotated[str, "Exact material, texture, Shader Graph, or Sub Graph path."],
    asset_kind: Annotated[Literal["material", "texture_importer", "shader_graph"], "Patch kind."],
    operations: Annotated[
        list[dict[str, Any]] | str,
        "Typed operation array or JSON string. Use an empty array to inspect the plan contract.",
    ],
    dry_run: Annotated[bool, "Plan without changing files or Unity objects."] = True,
    expected_sha256: Annotated[
        str | None,
        "Required current file SHA-256 for apply; rejects stale plans.",
    ] = None,
    copy_to: Annotated[
        str | None,
        "Optional project-owned Assets path copied before patching a vendor asset.",
    ] = None,
    allow_vendor_asset: Annotated[
        bool,
        "Explicitly allow direct vendor mutation instead of requiring copy_to.",
    ] = False,
    wait_timeout_seconds: Annotated[int, "Editor readiness timeout after apply (5-120)."] = 60,
) -> dict[str, Any]:
    try:
        parsed_operations = _parse_object(operations)
    except ValueError as exc:
        return {"success": False, "message": str(exc)}
    if not isinstance(parsed_operations, list):
        return {"success": False, "message": "operations must be a JSON array."}
    if not dry_run and not expected_sha256:
        return {
            "success": False,
            "message": "expected_sha256 is required when dry_run is false.",
        }

    result = await _send(ctx, "manage_rendering_authoring", {
        "asset_path": asset_path,
        "asset_kind": asset_kind,
        "operations": parsed_operations,
        "dry_run": dry_run,
        "expected_sha256": expected_sha256,
        "copy_to": copy_to,
        "allow_vendor_asset": allow_vendor_asset,
    })

    if dry_run or not result.get("success"):
        return result

    timeout = max(5, min(120, wait_timeout_seconds))
    ready, elapsed = await wait_for_editor_ready(ctx, timeout_s=float(timeout))
    result["editor_readiness"] = {
        "ready_for_tools": ready,
        "elapsed_seconds": elapsed,
    }
    if not ready:
        result["success"] = False
        result["message"] = (
            "Patch was applied, but editor readiness could not be confirmed before timeout."
        )
        return result

    validation_action = {
        "material": "inspect_material",
        "texture_importer": "inspect_texture",
        "shader_graph": "inspect_shader_graph",
    }[asset_kind]
    validation_key = {
        "material": "material_path",
        "texture_importer": "texture_path",
        "shader_graph": "shader_path",
    }[asset_kind]
    data = result.get("data") if isinstance(result.get("data"), dict) else {}
    effective_path = data.get("effective_asset_path", copy_to or asset_path)
    result["post_validation"] = await _send(ctx, "inspect_rendering", {
        "action": validation_action,
        validation_key: effective_path,
        "page_size": 25,
        "cursor": 0,
    })
    return result
