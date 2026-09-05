---
name: unity-mcp-orchestrator
description: Orchestrate Unity Editor via MCP (Model Context Protocol) tools and resources. Use when working with Unity projects through MCP for Unity - creating/modifying GameObjects, editing scripts, managing scenes, running tests, or any Unity Editor automation. Provides best practices, tool schemas, and workflow patterns for effective Unity-MCP integration.
---

# Unity-MCP Operator Guide

This skill helps you effectively use the Unity Editor with MCP tools and resources.

## Template Notice

Examples in `references/workflows.md` and `references/tools-reference.md` are reusable templates. They may be inaccurate across Unity versions, package setups (UGUI/TMP/Input System), and project-specific conventions. Please check console, compilation errors, or use screenshot after implementation.

Before applying a template:
- Validate targets/components first via resources and `find_gameobjects`.
- Treat names, enum values, and property payloads as placeholders to adapt.

## Quick Start: Resource-First Workflow

**Always read relevant resources before using tools.** This prevents errors and provides the necessary context.

```
1. Select the instance    → mcpforunity://instances, then set_active_instance when needed
2. Check editor state     → mcpforunity://editor/state
3. Discover tools         → manage_tools(action="search", query="...")
4. Activate one group     → manage_tools(action="activate", group="...")
5. Inspect the target     → resources, then find_gameobjects or another narrow query
6. Take action            → the activated tool using its live schema
7. Verify results         → editor state, filtered console reads, resources, and screenshots/tests as appropriate
```

## Tool Discovery and Bounded Results

The default server exposes only `manage_tools`, `set_active_instance`, `execute_custom_tool`, and `manage_script_capabilities`. Search first, then activate only the group needed for the current task:

```python
manage_tools(action="search", query="scene")
manage_tools(action="activate", group="core")
```

Use `mcpforunity://tool-groups` for a compact group catalog. The live MCP schema is authoritative; this skill's references are curated operating guidance rather than an exhaustive copy of every tool schema.

Large results may return a `result_uri` such as `mcpforunity://results/{result_id}/0`. Read that URI and follow `next_uri` until it is null. Do not repeat the original broad call merely to force the payload inline.

## Critical Best Practices

### 1. After Writing/Editing Scripts: Wait for Compilation and Check Console

```python
# After create_script or script_apply_edits:
# Both tools already trigger AssetDatabase.ImportAsset + RequestScriptCompilation automatically.
# No need to call refresh_unity — just wait for compilation to finish, then check console.

# 1. Poll editor state until compilation completes
# Read mcpforunity://editor/state → wait until is_compiling == false

# 2. Check for compilation errors
read_console(types=["error"], count=10, include_stacktrace=True)
```

**Why:** Unity must compile scripts before they're usable. `create_script` and `script_apply_edits` already trigger import and compilation automatically — calling `refresh_unity` afterward is redundant.

### 2. Use `batch_execute` to Reduce Round Trips

```python
# One MCP round trip for several ordered Unity commands
batch_execute(
    commands=[
        {"tool": "manage_gameobject", "params": {"action": "create", "name": "Cube1", "primitive_type": "Cube"}},
        {"tool": "manage_gameobject", "params": {"action": "create", "name": "Cube2", "primitive_type": "Cube"}},
        {"tool": "manage_gameobject", "params": {"action": "create", "name": "Cube3", "primitive_type": "Cube"}}
    ],
    fail_fast=True
)
```

**Max 25 commands per batch by default (configurable in Unity MCP Tools window, max 100).** Use `fail_fast=True` for dependent operations.

Unity executes the batch as one main-thread sequence. The legacy `parallel` and `max_parallelism` fields are deprecated compatibility inputs and do not make Unity commands concurrent. A batch is not rollback-transactional: earlier successful commands remain applied if a later command fails.

**Tip:** Also use `batch_execute` for discovery — batch multiple `find_gameobjects` calls instead of calling them one at a time:
```python
batch_execute(commands=[
    {"tool": "find_gameobjects", "params": {"search_term": "Camera", "search_method": "by_component"}},
    {"tool": "find_gameobjects", "params": {"search_term": "Player", "search_method": "by_tag"}},
    {"tool": "find_gameobjects", "params": {"search_term": "GameManager", "search_method": "by_name"}}
])
```

### 3. Use Screenshots to Verify Visual Results

```python
# Basic screenshot (saves to Assets/, returns file path only)
manage_camera(action="screenshot")

# Inline screenshot (returns base64 PNG directly to the AI)
manage_camera(action="screenshot", include_image=True)

# Use a specific camera and cap resolution for smaller payloads
manage_camera(action="screenshot", camera="MainCamera", include_image=True, max_resolution=512)

# Batch surround: captures front/back/left/right/top/bird_eye around the scene
manage_camera(action="screenshot", batch="surround", max_resolution=256)

# Batch surround centered on a specific object
manage_camera(action="screenshot", batch="surround", view_target="Player", max_resolution=256)

# Positioned screenshot: place a temp camera and capture in one call
manage_camera(action="screenshot", view_target="Player", view_position=[0, 10, -10], max_resolution=512)

# Scene View screenshot: capture what the developer sees in the editor
manage_camera(action="screenshot", capture_source="scene_view", include_image=True)

# Scene View framed on a specific object
manage_camera(action="screenshot", capture_source="scene_view", view_target="Canvas", include_image=True)
```

**Best practices for AI scene understanding:**
- Use `include_image=True` when you need to *see* the scene, not just save a file.
- Use `batch="surround"` for a comprehensive overview (6 angles, one command).
- Use `view_target`/`view_position` to capture from a specific viewpoint without needing a scene camera.
- Use `capture_source="scene_view"` to see the editor viewport (gizmos, wireframes, grid).
- Keep `max_resolution` at 256–512 to balance quality vs. token cost.

```python
# Agentic camera loop: point, shoot, analyze
manage_gameobject(action="look_at", target="MainCamera", look_at_target="Player")
manage_camera(action="screenshot", camera="MainCamera", include_image=True, max_resolution=512)
# → Analyze image, decide next action

# Multi-view screenshot (6-angle contact sheet)
manage_camera(action="screenshot_multiview", max_resolution=480)

# Scene View for editor-level inspection (shows gizmos, debug overlays, etc.)
manage_camera(action="screenshot", capture_source="scene_view", view_target="Player", include_image=True)
```

### 4. Check Console After Major Changes

```python
read_console(
    action="get",
    types=["error", "warning"],  # Focus on problems
    count=10,
    format="detailed"
)
```

### 5. Always Check `editor_state` Before Complex Operations

```python
# Read mcpforunity://editor/state to check:
# - compilation.is_compiling: Wait if true
# - compilation.is_domain_reload_pending: Wait if true
# - advice.ready_for_tools: Only proceed if true
# - advice.blocking_reasons: Why tools might fail
# - staleness.is_stale: A stale snapshot is not readiness proof
```

## Mutation Safety Contracts

### Scene recovery, additive work, save, and Play Mode

- Inspect recovery scenes with `manage_scene(action="load_preview")`, then close the returned lease with `close_preview_scene`. Never load recovery or `Temp/__Backupscenes/*.backup` files additively into the authoring scene.
- Additive loads default to `scene_intent="temporary_inspection"`. Their lease blocks MCP save and Play Mode until closed. Use `scene_intent="authoring"` only for a deliberate persistent multi-scene composition.
- When multiple normal scenes are loaded, identify the save target with `scene_name` or `scene_path`. Save and MCP Play fail closed on cross-scene references.
- Scene-changing commands and editor Play/pause/stop commands write a bounded journal at `Library/MCPForUnity/CommandJournal/scene-commands.jsonl` with request/session/Unity correlation and before/after scene fingerprints. Use it for recovery and audit evidence; do not treat it as mutation authority.

### Prefab creation and lifecycle

- Prefab creation in Play Mode is blocked unless `allow_play_mode_create=true` is explicitly supplied. Opt in only when `Awake`/`OnEnable`, singleton, and persistence effects are intentional.
- `instance_policy` accepts `always_create` (ordinary default), `fail_if_same_prefab`, or `reuse_same_prefab`. Prefer reject or reuse for singleton/bootstrap prefabs.
- Interpret stable failure codes before diagnosing corruption: `play_mode_create_blocked`, `prefab_instance_exists`, and `prefab_instance_destroyed`. The last means the instance died during a lifecycle phase; inspect its `hint`, `data`, and bounded diagnostics.
- A successful creation may still contain `warnings`. Preserve and inspect `code`, `message`, `hint`, `data`, and `warnings` rather than collapsing the response to one text field.

### Reflected component data

Component resources omit obsolete members, unsafe/unbounded values, and state-invalid getters before invocation. In particular, `AudioSource.time` and `timeSamples` are omitted unless the source has a real `AudioClip`; guarded `NavMeshAgent` properties remain unavailable off-mesh. Check `serialization.omittedProperties` and `serialization.truncated` before concluding that a property does not exist or has no value.

## Parameter Type Conventions

These are common patterns, not strict guarantees. `manage_components.set_property` payload shapes can vary by component/property; if a template fails, inspect the component resource payload and adjust.

### Vectors (position, rotation, scale, color)
```python
# Both forms accepted:
position=[1.0, 2.0, 3.0]        # List
position="[1.0, 2.0, 3.0]"     # JSON string
```

### Booleans
```python
# Both forms accepted:
include_inactive=True           # Boolean
include_inactive="true"         # String
```

### Colors
```python
# Auto-detected format:
color=[255, 0, 0, 255]         # 0-255 range
color=[1.0, 0.0, 0.0, 1.0]    # 0.0-1.0 normalized (auto-converted)
```

### Paths
```python
# Assets-relative (default):
path="Assets/Scripts/MyScript.cs"

# URI forms:
uri="mcpforunity://path/Assets/Scripts/MyScript.cs"
uri="file:///full/path/to/file.cs"
```

## Core Tool Categories

| Category | Key Tools | Use For |
|----------|-----------|---------|
| **Discovery** | `manage_tools`, `set_active_instance`, `execute_custom_tool`, `mcpforunity://tool-groups` | Search and activate only the required tool group, select the target Editor, and invoke active-project extensions. |
| **Scene** | `manage_scene`, `find_gameobjects` | Scene operations, isolated recovery previews, leases, and object discovery. |
| **Objects** | `manage_gameobject`, `manage_components` | Create/modify GameObjects and components with lifecycle-safe prefab handling. |
| **Scripts** | `create_script`, `script_apply_edits`, `apply_text_edits`, `find_in_file`, `get_sha`, `validate_script`, `delete_script`, `manage_script_capabilities` | C# code management with SHA/precondition guards and automatic import/compile on create/edit. |
| **Assets/Builds** | `manage_asset`, `manage_prefabs`, `manage_material`, `manage_build`, `manage_packages` | Assets, materials, prefab contents, Player builds, and packages. **Instantiate prefabs with `manage_gameobject`.** |
| **Editor** | `manage_editor`, `execute_menu_item`, `read_console` | Editor control, undo/redo, package deployment, and filtered diagnostics. |
| **Testing** | `run_tests`, `get_test_job`, `manage_validation` | Unity Test Framework and durable validation records. |
| **Batch** | `batch_execute` | Reduce round trips for ordered Unity commands; not parallel and not rollback-transactional. |
| **Camera** | `manage_camera` | Camera management (Unity Camera + Cinemachine). **Tier 1** (always available): create, target, lens, priority, list, screenshot. **Tier 2** (requires `com.unity.cinemachine`): brain, body/aim/noise pipeline, extensions, blending, force/release. 7 presets: follow, third_person, freelook, dolly, static, top_down, side_scroller. Resource: `mcpforunity://scene/cameras`. Use `ping` to check Cinemachine availability. See [tools-reference.md](references/tools-reference.md#camera-tools). |
| **Graphics** | `manage_graphics` | Rendering and post-processing management. 33 actions across 5 groups: **Volume** (create/configure volumes and effects, URP/HDRP), **Bake** (lightmaps, light probes, reflection probes, Edit mode only), **Stats** (draw calls, batches, memory), **Pipeline** (quality levels, pipeline settings), **Features** (URP renderer features: add, remove, toggle, reorder). Resources: `mcpforunity://scene/volumes`, `mcpforunity://rendering/stats`, `mcpforunity://pipeline/renderer-features`. Use `ping` to check pipeline status. See [tools-reference.md](references/tools-reference.md#graphics-tools). |
| **Physics** | `manage_physics` | Manage 3D and 2D physics (21 actions). Settings, collision matrix, materials, joints (14 types). Queries: `raycast`, `raycast_all`, `linecast`, `shapecast` (sphere/box/capsule sweep), `overlap`. Forces: `apply_force` (AddForce/AddTorque/AddExplosionForce with ForceMode). Rigidbody: `get_rigidbody`, `configure_rigidbody` (mass, drag, gravity, constraints, collision detection). Validation: scene-wide checks. Simulation: `simulate_step` in edit mode. See [tools-reference.md](references/tools-reference.md#physics-tools). |
| **ProBuilder** | `manage_probuilder` | 3D modeling, mesh editing, complex geometry. **When `com.unity.probuilder` is installed, prefer ProBuilder shapes over primitive GameObjects** for editable geometry, multi-material faces, or complex shapes. Supports 12 shape types, face/edge/vertex editing, smoothing, and per-face materials. See [ProBuilder Guide](references/probuilder-guide.md). |
| **UI** | `manage_ui`, `batch_execute` with `manage_gameobject` + `manage_components` | **UI Toolkit**: Use `manage_ui` to create UXML/USS files, attach UIDocument, inspect visual trees. **uGUI (Canvas)**: Use `batch_execute` for Canvas, Panel, Button, Text, Slider, Toggle, Input Field. **Read `mcpforunity://project/info` first** to detect uGUI/TMP/Input System/UI Toolkit availability. (see [UI workflows](references/workflows.md#ui-creation-workflows)) |
| **Animation/VFX** | `manage_animation`, `manage_vfx`, `manage_shader`, `manage_texture` | Animator/clip work and opt-in VFX/shader/texture operations. |
| **Rendering inspection** | `inspect_render_target`, `inspect_material`, `inspect_texture`, `inspect_shader_graph`, `validate_render_contract`, `sample_material`, `render_probe`, `profile_render_target` | Read-only owner/material/texture/shader/probe evidence. |
| **Rendering authoring** | `manage_rendering_authoring` | Opt-in transactional material, importer, and Shader Graph patches; plan/dry-run before apply. |
| **Scripting extensions** | `manage_scriptable_object`, `execute_code` | ScriptableObject management and explicitly authorized in-Editor C# execution. |
| **Profiling** | `manage_profiler` | Profiler counters, captures, memory snapshots, and Frame Debugger evidence. |
| **Asset generation** | `generate_image`, `generate_audio`, `generate_model`, `import_model`, `import_model_file` | Opt-in generated/imported assets using configured providers. |
| **Docs** | `unity_reflect`, `unity_docs` | API verification and documentation lookup. **`unity_reflect`** inspects live C# APIs via reflection (requires Unity connection): `search` types across assemblies, `get_type` for member summary, `get_member` for full signatures. **`unity_docs`** fetches official docs from docs.unity3d.com (no Unity connection needed): `get_doc` (ScriptReference), `get_manual` (Manual pages), `get_package_doc` (package docs), `lookup` (parallel search all sources + project assets). **Trust hierarchy: reflection > project assets > docs.** Workflow: `unity_reflect` search -> get_type -> get_member -> `unity_docs` lookup. See [tools-reference.md](references/tools-reference.md#docs-tools). |

## Common Workflows

### Creating a New Script and Using It

```python
# 1. Create the script (automatically triggers import + compilation)
create_script(
    path="Assets/Scripts/PlayerController.cs",
    contents="using UnityEngine;\n\npublic class PlayerController : MonoBehaviour\n{\n    void Update() { }\n}"
)

# 2. Wait for compilation to finish
# Read mcpforunity://editor/state → wait until is_compiling == false

# 3. Check for compilation errors
read_console(types=["error"], count=10)

# 4. Only then attach to GameObject
manage_gameobject(action="modify", target="Player", components_to_add=["PlayerController"])
```

### Finding and Modifying GameObjects

```python
# 1. Find by name/tag/component (returns IDs only)
result = find_gameobjects(search_term="Enemy", search_method="by_tag", page_size=50)

# 2. Get full data via resource
# mcpforunity://scene/gameobject/{instance_id}

# 3. Modify using the ID
manage_gameobject(action="modify", target=instance_id, position=[10, 0, 0])
```

### Running and Monitoring Tests

```python
# 1. Start test run (async)
result = run_tests(mode="EditMode", test_names=["MyTests.TestSomething"])
job_id = result["job_id"]

# 2. Poll for completion
result = get_test_job(job_id=job_id, wait_timeout=60, include_failed_tests=True)
```

## Pagination Pattern

Large queries return paginated results. Always follow `next_cursor`:

```python
cursor = 0
all_items = []
while True:
    result = manage_scene(action="get_hierarchy", page_size=50, cursor=cursor)
    all_items.extend(result["data"]["items"])
    if not result["data"].get("next_cursor"):
        break
    cursor = result["data"]["next_cursor"]
```

## Multi-Instance Workflow

When multiple Unity Editors are running:

```python
# 1. List instances via resource: mcpforunity://instances
# 2. Set active instance
set_active_instance(instance="MyProject@abc123")
# 3. All subsequent calls route to that instance
```

## Error Recovery

| Symptom | Cause | Solution |
|---------|-------|----------|
| Tools return "busy" | Compilation in progress | Wait, check `editor_state` |
| Tool is not listed | Its group is not active | `manage_tools(action="search", query="...")`, then activate the matching group |
| "stale_file" error | File changed since SHA | Re-fetch SHA with `get_sha`, retry |
| Connection lost | Domain reload | Wait ~5s, reconnect |
| Commands fail silently | Wrong instance | Check `set_active_instance` |
| Prefab reported destroyed | Lifecycle code destroyed the new instance | Inspect `code`, `hint`, `data.phase`, and `warnings`; do not assume asset corruption |
| Save or Play is blocked | Temporary scene lease or cross-scene reference exists | Close the lease or repair the reference; do not bypass the guard |
| Deployed Python behavior is unchanged | The running MCP server still has old modules loaded | Treat installed files and running-process activation separately; restart only when authorized |

## Reference Files

For detailed schemas and examples:

- **[tools-reference.md](references/tools-reference.md)**: Complete tool documentation with all parameters
- **[resources-reference.md](references/resources-reference.md)**: All available resources and their data
- **[workflows.md](references/workflows.md)**: Extended workflow examples and patterns
