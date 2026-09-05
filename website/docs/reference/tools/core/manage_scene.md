---
title: manage_scene
sidebar_label: manage_scene
description: "Performs CRUD operations on Unity scenes."
---

# `manage_scene`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_scene`

## Description

Performs CRUD operations on Unity scenes. Read-only actions: get_hierarchy, get_active, get_build_settings, get_loaded_scenes, scene_view_frame. Modifying actions: create (with optional template), load, load_preview, save, close_scene, close_preview_scene, set_active_scene, move_to_scene, validate (with optional auto_repair). Use load_preview for recovery/backup inspection. Additive load defaults to a temporary lease that blocks save and Play Mode until closed; pass scene_intent='authoring' only for an intentional multi-scene setup. When multiple normal scenes are loaded, save requires scene_name or scene_path. For build settings management (add/remove/enable scenes), use manage_build(action='scenes'). For screenshots, use manage_camera (screenshot, screenshot_multiview actions).

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['create', 'load', 'load_preview', 'save', 'get_hierarchy', 'get_active', 'get_build_settings', 'scene_view_frame', 'close_scene', 'close_preview_scene', 'set_active_scene', 'get_loaded_scenes', 'move_to_scene', 'validate']` | yes | Perform CRUD operations on Unity scenes and control the Scene View camera. |
| `name` | `str \| None` | — | Scene name. |
| `path` | `str \| None` | — | Scene path. |
| `build_index` | `int \| str \| None` | — | Unity build index (quote as string, e.g., '0'). |
| `scene_view_target` | `str \| int \| None` | — | GameObject reference for scene_view_frame (name, path, or instance ID). |
| `parent` | `str \| int \| None` | — | Optional parent GameObject reference (name/path/instanceID) to list direct children. |
| `page_size` | `int \| str \| None` | — | Page size for get_hierarchy paging. |
| `cursor` | `int \| str \| None` | — | Opaque cursor for paging (offset). |
| `max_nodes` | `int \| str \| None` | — | Hard cap on returned nodes per request (safety). |
| `max_depth` | `int \| str \| None` | — | Accepted for forward-compatibility; current paging returns a single level. |
| `max_children_per_node` | `int \| str \| None` | — | Child paging hint (safety). |
| `include_transform` | `bool \| str \| None` | — | If true, include local transform in node summaries. |
| `scene_name` | `str \| None` | — | Scene name for multi-scene operations. |
| `scene_path` | `str \| None` | — | Full scene path (e.g. 'Assets/Scenes/Level2.unity'). |
| `target` | `str \| int \| None` | — | GameObject reference (name, path, or instanceID) for move_to_scene. |
| `remove_scene` | `bool \| str \| None` | — | For close_scene: true to fully remove, false to just unload. |
| `additive` | `bool \| str \| None` | — | For load: true to open scene additively. Recovery/backup scenes must use load_preview. |
| `scene_intent` | `Literal['temporary_inspection', 'authoring'] \| None` | — | For additive load: temporary_inspection (default) creates a blocking lease; authoring opts into a durable multi-scene setup. |
| `lease_id` | `str \| None` | — | For close_preview_scene: lease ID returned by load_preview. |
| `template` | `str \| None` | — | For create: scene template ('empty', 'default', '3d_basic', '2d_basic'). Omit for empty scene. |
| `auto_repair` | `bool \| str \| None` | — | For validate: true to auto-fix missing scripts (undoable). |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
### Load a scene from `Assets/Scenes/`

> Open `Assets/Scenes/MainMenu.unity`.

```json
{
  "action": "load",
  "path": "Scenes/MainMenu.unity"
}
```

Paths are relative to `Assets/`. Forward slashes only.

### Get the scene hierarchy (paged)

> List every GameObject in the active scene.

```json
{
  "action": "get_hierarchy",
  "page_size": 100
}
```

Returns up to `page_size` entries plus a `next_cursor` for the remainder. Always page large hierarchies.

### Save the active scene

> Save the active scene under its existing path.

```json
{ "action": "save" }
```

If multiple normal scenes are loaded, identify the target explicitly:

```json
{
  "action": "save",
  "scene_path": "Assets/Scenes/Main.unity"
}
```

Save fails before touching disk if Unity detects cross-scene references or a temporary additive-scene lease is open.

### Create a scene from a template

> Make a new 3D scene called `Lab`.

```json
{
  "action": "create",
  "path": "Scenes/Lab.unity",
  "template": "3d_basic"
}
```

Other templates: `2d_basic`, `default`, `empty`.

### Additive multi-scene editing

> Load `Scenes/Boss.unity` additively while keeping the current scene open.

```json
{
  "action": "load",
  "path": "Scenes/Boss.unity",
  "additive": true,
  "scene_intent": "authoring"
}
```

Use `set_active_scene`, `close_scene`, and `move_to_scene` to compose multi-scene setups.

### Inspect a recovery scene safely

> Open a recovery copy in an isolated preview scene without adding it to the normal loaded-scene set.

```json
{
  "action": "load_preview",
  "path": "Assets/_Recovery/Recovered.unity"
}
```

Close it with the returned lease ID:

```json
{
  "action": "close_preview_scene",
  "lease_id": "<returned leaseId>"
}
```

Unity emergency backups such as `Temp/__Backupscenes/0.backup` are also accepted. The bridge creates a narrowly owned `.unity` shadow under `Temp/MCPForUnity/PreviewScenes`, records both paths in the lease, and deletes the shadow when that lease closes.

Ordinary additive loads default to `scene_intent="temporary_inspection"`; that lease blocks scene saves and Play Mode until the loaded scene is closed.
<!-- examples:end -->

