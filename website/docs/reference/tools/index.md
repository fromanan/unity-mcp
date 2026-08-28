---
title: Tool reference
sidebar_label: Tools
sidebar_class_name: sidebar-hidden
slug: /reference/tools
description: Auto-generated catalog of every MCP for Unity tool, grouped by domain.
---

# Tool reference

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

Every tool MCP for Unity exposes, generated directly from the Python `@mcp_for_unity_tool` registry under `Server/src/services/tools/`.

## `animation` &nbsp; (1 tool)
Animator control & AnimationClip creation
- **[`manage_animation`](./animation/manage_animation.md)** — Manage Unity animation: Animator control and AnimationClip creation.

## `asset_gen` &nbsp; (5 tools)
AI asset generation – 3D model gen/import, 2D image gen & audio gen (bring-your-own-key)
- **[`generate_audio`](./asset_gen/generate_audio.md)** — Generate audio (sound effects and background music) with fal.ai models and import them as AudioClips into the Unity project.
- **[`generate_image`](./asset_gen/generate_image.md)** — Generate 2D images with AI providers (fal.ai, OpenRouter) and import them as textures/sprites into the Unity project.
- **[`generate_model`](./asset_gen/generate_model.md)** — Generate 3D models with AI providers (Tripo, Meshy) and import them into the Unity project.
- **[`import_model`](./asset_gen/import_model.md)** — Import 3D models from the Sketchfab marketplace into the Unity project.
- **[`import_model_file`](./asset_gen/import_model_file.md)** — Import a local 3D model file that already exists on disk (e.g. an FBX/OBJ/glTF exported from Blender or another DCC tool) into the Unity project.

## `core` &nbsp; (30 tools)
Essential scene, script, asset and editor tools
- **[`apply_text_edits`](./core/apply_text_edits.md)** — Apply small text edits to a C# script identified by URI.
- **[`batch_execute`](./core/batch_execute.md)** — Executes multiple MCP commands in a single batch for dramatically better performance.
- **[`create_script`](./core/create_script.md)** — Create a new C# script at the given project path.
- **[`debug_request_context`](./core/debug_request_context.md)** — Return the current FastMCP request context details (client_id, session_id, and meta dump).
- **[`delete_script`](./core/delete_script.md)** — Delete a C# script by URI or Assets-relative path.
- **[`execute_custom_tool`](./core/execute_custom_tool.md)** — Execute a project-scoped custom tool registered by Unity.
- **[`execute_menu_item`](./core/execute_menu_item.md)** — Execute a Unity menu item by path.
- **[`find_gameobjects`](./core/find_gameobjects.md)** — Search for GameObjects in the scene by name, tag, layer, component type, or path.
- **[`find_in_file`](./core/find_in_file.md)** — Searches a file with a regex pattern and returns line numbers and excerpts.
- **[`get_sha`](./core/get_sha.md)** — Get SHA256 and basic metadata for a Unity C# script without returning file contents.
- **[`manage_asset`](./core/manage_asset.md)** — Performs asset operations (import, create, modify, delete, etc.) in Unity.
- **[`manage_build`](./core/manage_build.md)** — Manage Unity player builds — trigger builds, switch platforms, configure settings, manage build scenes and profiles, run batch builds across platforms.
- **[`manage_camera`](./core/manage_camera.md)** — Manage cameras (Unity Camera + Cinemachine).
- **[`manage_components`](./core/manage_components.md)** — Add, remove, or set properties on components attached to GameObjects.
- **[`manage_editor`](./core/manage_editor.md)** — Controls and queries the Unity editor's state and settings.
- **[`manage_gameobject`](./core/manage_gameobject.md)** — Performs CRUD operations on GameObjects.
- **[`manage_graphics`](./core/manage_graphics.md)** — Manage rendering graphics: volumes, post-processing, light baking, rendering stats, pipeline settings, and URP renderer features.
- **[`manage_material`](./core/manage_material.md)** — Manages Unity materials (set properties, colors, shaders, etc).
- **[`manage_packages`](./core/manage_packages.md)** — Manage Unity packages: query, install, remove, embed, and configure registries.
- **[`manage_physics`](./core/manage_physics.md)** — Manage physics settings, collision matrix, materials, joints, queries, and validation.
- **[`manage_prefabs`](./core/manage_prefabs.md)** — Manages Unity Prefab assets.
- **[`manage_scene`](./core/manage_scene.md)** — Performs CRUD operations on Unity scenes.
- **[`manage_script`](./core/manage_script.md)** — Compatibility router for legacy script operations.
- **[`manage_script_capabilities`](./core/manage_script_capabilities.md)** — Get manage_script capabilities (supported ops, limits, and guards).
- **[`manage_tools`](./core/manage_tools.md)** — Search and toggle per-session tool groups.
- **[`read_console`](./core/read_console.md)** — Gets messages from or clears the Unity Editor console.
- **[`refresh_unity`](./core/refresh_unity.md)** — Request a Unity asset database refresh and optionally a script compilation.
- **[`script_apply_edits`](./core/script_apply_edits.md)** — Structured C# edits (methods/classes) with safer boundaries - prefer this over raw text.
- **[`set_active_instance`](./core/set_active_instance.md)** — Set the active Unity instance for this client/session.
- **[`validate_script`](./core/validate_script.md)** — Validate a C# script and return diagnostics.

## `docs` &nbsp; (2 tools)
Unity API reflection and documentation lookup
- **[`unity_docs`](./docs/unity_docs.md)** — Fetch official Unity documentation from docs.unity3d.com.
- **[`unity_reflect`](./docs/unity_reflect.md)** — Inspect Unity's live C# API via reflection.

## `probuilder` &nbsp; (1 tool)
ProBuilder 3D modeling – requires com.unity.probuilder package
- **[`manage_probuilder`](./probuilder/manage_probuilder.md)** — Manage ProBuilder meshes for in-editor 3D modeling.

## `profiling` &nbsp; (1 tool)
Unity Profiler session control, counters, memory snapshots & Frame Debugger
- **[`manage_profiler`](./profiling/manage_profiler.md)** — Unity Profiler session control, counter reads, memory snapshots, and Frame Debugger.

## `rendering_authoring` &nbsp; (1 tool)
Opt-in transactional material, texture-importer & Shader Graph authoring
- **[`manage_rendering_authoring`](./rendering_authoring/manage_rendering_authoring.md)** — Plan or apply a transactional material, texture-importer, or Shader Graph patch.

## `rendering_inspect` &nbsp; (8 tools)
Read-only renderer, material, texture, Shader Graph, render-contract & probe inspection
- **[`inspect_material`](./rendering_inspect/inspect_material.md)** — Inspect one material by exact asset path, including path/GUID, shader identity and kind, typed current/default values, texture path/GUID and tiling, keywords, passes, queue/surface state, GI/instancing/SRP Batcher evidence, and paged liv…
- **[`inspect_render_target`](./rendering_inspect/inspect_render_target.md)** — Inspect the actual render-owner closure for a scene object: renderers, submeshes, material slots, material property blocks, LOD membership, lightmap state, and package/asset ownership.
- **[`inspect_shader_graph`](./rendering_inspect/inspect_shader_graph.md)** — Inspect a ShaderLab, Shader Graph, or Sub Graph asset by exact path.
- **[`inspect_texture`](./rendering_inspect/inspect_texture.md)** — Inspect one texture by exact asset path.
- **[`profile_render_target`](./rendering_inspect/profile_render_target.md)** — Profile one scene render target with static renderer/material/pass/mesh evidence and a paged Frame Debugger snapshot filtered to its renderer instance IDs when Frame Debugger data is available.
- **[`render_probe`](./rendering_inspect/render_probe.md)** — Capture a deterministic color or wireframe render probe from an existing camera.
- **[`sample_material`](./rendering_inspect/sample_material.md)** — Render one exact material in an isolated, deterministic Editor preview.
- **[`validate_render_contract`](./rendering_inspect/validate_render_contract.md)** — Validate the renderer-material-shader-texture closure for an exact material or scene target.

## `scripting_ext` &nbsp; (2 tools)
ScriptableObject management
- **[`execute_code`](./scripting_ext/execute_code.md)** — Execute arbitrary C# code inside the Unity Editor.
- **[`manage_scriptable_object`](./scripting_ext/manage_scriptable_object.md)** — Creates and modifies ScriptableObject assets using Unity SerializedObject property paths.

## `testing` &nbsp; (3 tools)
Test runner & async test jobs
- **[`get_test_job`](./testing/get_test_job.md)** — Polls an async Unity test job by job_id.
- **[`manage_validation`](./testing/manage_validation.md)** — Creates and records durable validation runs.
- **[`run_tests`](./testing/run_tests.md)** — Starts a Unity test run asynchronously and returns a job_id immediately.

## `ui` &nbsp; (1 tool)
UI Toolkit (UXML, USS, UIDocument)
- **[`manage_ui`](./ui/manage_ui.md)** — Manages Unity UI Toolkit elements (UXML documents, USS stylesheets, UIDocument components).

## `vfx` &nbsp; (3 tools)
Visual effects – VFX Graph, shaders, procedural textures
- **[`manage_shader`](./vfx/manage_shader.md)** — Manages shader scripts in Unity (create, read, update, delete).
- **[`manage_texture`](./vfx/manage_texture.md)** — Procedural texture generation for Unity.
- **[`manage_vfx`](./vfx/manage_vfx.md)** — Manage Unity VFX components (ParticleSystem, VisualEffect, LineRenderer, TrailRenderer).

