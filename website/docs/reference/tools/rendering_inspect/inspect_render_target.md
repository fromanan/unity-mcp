---
title: inspect_render_target
sidebar_label: inspect_render_target
description: "Inspect the actual render-owner closure for a scene object: renderers, submeshes, material slots, material property blocks, LOD membership, lightmap state, and package/asset ownership."
---

# `inspect_render_target`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `rendering_inspect` &nbsp;·&nbsp; **Module:** `services.tools.rendering_workflow`

## Description

Inspect the actual render-owner closure for a scene object: renderers, submeshes, material slots, material property blocks, LOD membership, lightmap state, and package/asset ownership. Results are paged and read-only.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `target` | `str` | yes | GameObject name, hierarchy path, or instance ID. |
| `include_children` | `bool` | — | Inspect child renderers. |
| `include_inactive` | `bool` | — | Include inactive child renderers. |
| `page_size` | `int` | — | Renderer records per page (1-100). |
| `cursor` | `int` | — | Zero-based renderer cursor. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

