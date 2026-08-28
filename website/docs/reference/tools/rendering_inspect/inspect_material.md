---
title: inspect_material
sidebar_label: inspect_material
description: "Inspect one material by exact asset path, including path/GUID, shader identity and kind, typed current/default values, texture path/GUID and tiling, keywords, passes, queue/surface state, GI/instancing/SRP Batcher evidence, and paged liv…"
---

# `inspect_material`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `rendering_inspect` &nbsp;·&nbsp; **Module:** `services.tools.rendering_workflow`

## Description

Inspect one material by exact asset path, including path/GUID, shader identity and kind, typed current/default values, texture path/GUID and tiling, keywords, passes, queue/surface state, GI/instancing/SRP Batcher evidence, and paged live renderer consumers.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `material_path` | `str` | yes | Exact material path under Assets/ or Packages/. |
| `include_consumers` | `bool` | — | Include live scene renderer consumers. |
| `page_size` | `int` | — | Consumer records per page (1-100). |
| `cursor` | `int` | — | Zero-based consumer cursor. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

