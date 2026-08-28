---
id: tool-groups
slug: /guides/tool-groups
title: Tool Groups and manage_tools
sidebar_label: Tool Groups
description: Keep MCP prompts small by discovering and activating only the Unity tool groups needed for the current task.
---

# Tool Groups

MCP for Unity registers 58 tools, but exposing every schema on every turn balloons the prompt and dilutes routing decisions. The default `bootstrap` profile therefore exposes only four routing/meta-tools. Discover a capability, activate its group, use it, then deactivate it when the phase is complete.

## The groups

| Group | Default | Description |
|---|---|---|
| `core` | off | Essential scene, script, asset, and editor tools. |
| `animation` | off | Animator control, AnimationClip creation. |
| `ui` | off | UI Toolkit — UXML, USS, UIDocument. |
| `vfx` | off | VFX Graph, shaders, procedural textures. |
| `scripting_ext` | off | ScriptableObject management. |
| `testing` | off | Test runner and async test jobs. |
| `probuilder` | off | ProBuilder 3D modeling. Requires `com.unity.probuilder` package. |
| `profiling` | off | Profiler session control, counters, memory snapshots, Frame Debugger. |
| `docs` | off | Unity API reflection and documentation lookup. |
| `rendering_inspect` | off | Read-only renderer, material, texture, Shader Graph, render-contract, and probe inspection. |
| `rendering_authoring` | off | Transactional material, texture-importer, and Shader Graph authoring. |
| `asset_gen` | off | AI model, image, and audio generation/import tools. |

## Enabling a group

Use the `manage_tools` meta-tool from your prompt:

> Activate the `vfx` group so we can author shaders.

The assistant calls:

```
manage_tools(action="activate", group="vfx")
```

After activation, the group's tools appear in the next tool listing and are usable for the remainder of the session.

## Listing what's available

```
manage_tools(action="list_groups")
```

Returns compact group status and tool counts. Add `include_tools=true` only when you need every tool name:

```
manage_tools(action="list_groups", include_tools=true)
```

For lower-token discovery, search first:

```
manage_tools(action="search", query="shader graph")
```

## Deactivating

```
manage_tools(action="deactivate", group="vfx")
```

Useful when a group's tools are confusing the assistant — e.g., `manage_shader` and `manage_material` both apply to materials in different ways. Disabling the one you're not using keeps the assistant focused.

## Other actions

- `sync` — refreshes visibility from the Unity Editor's per-tool toggle UI. Use after toggling tools in `Window > MCP for Unity > Tools`.
- `reset` — restores the selected startup profile.

## Why this exists

Three reasons:

1. **Prompt economy**: each visible tool adds tokens to every assistant call. Hiding what you're not using is real money saved at scale.
2. **Routing clarity**: the assistant chooses among only the capabilities relevant to the current phase.
3. **Package hygiene**: tools in `probuilder` only work if `com.unity.probuilder` is installed; hiding them by default avoids confusing errors.

## Server vs. session state

- The Unity Editor maintains a per-tool **toggle UI** (`Window > MCP for Unity > Tools`) that advertises which Unity-side handlers are available.
- The `manage_tools` meta-tool adds a **per-MCP-session** visibility gate, so different agents can expose different groups against the same server.

`sync` reconciles the two in stdio mode by pulling Editor toggle states into the current session. HTTP sessions receive updated registrations and `tools/list_changed` notifications automatically.

## Compatibility profile

Set `UNITY_MCP_DEFAULT_TOOL_PROFILE=compat` before server startup to expose `core`, `rendering_inspect`, and `testing` by default. This eases migration for clients that expect the larger historical catalog; `bootstrap` remains the token-efficient default.

## Related reference

- [`manage_tools`](/reference/tools/core/manage_tools) — full tool reference
- [`tool_groups` resource](/reference/resources) — discoverable group catalog
