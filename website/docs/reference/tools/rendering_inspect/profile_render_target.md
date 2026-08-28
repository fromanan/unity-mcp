---
title: profile_render_target
sidebar_label: profile_render_target
description: "Profile one scene render target with static renderer/material/pass/mesh evidence and a paged Frame Debugger snapshot filtered to its renderer instance IDs when Frame Debugger data is available."
---

# `profile_render_target`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `rendering_inspect` &nbsp;·&nbsp; **Module:** `services.tools.rendering_workflow`

## Description

Profile one scene render target with static renderer/material/pass/mesh evidence and a paged Frame Debugger snapshot filtered to its renderer instance IDs when Frame Debugger data is available. Reports proof levels.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `target` | `str` | yes | GameObject name, hierarchy path, or instance ID. |
| `include_frame_debugger` | `bool` | — | Include currently captured Frame Debugger events. |
| `page_size` | `int` | — | Frame Debugger records per page (1-100). |
| `cursor` | `int` | — | Zero-based Frame Debugger cursor. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

