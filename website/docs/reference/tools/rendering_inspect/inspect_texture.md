---
title: inspect_texture
sidebar_label: inspect_texture
description: "Inspect one texture by exact asset path."
---

# `inspect_texture`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `rendering_inspect` &nbsp;·&nbsp; **Module:** `services.tools.rendering_workflow`

## Description

Inspect one texture by exact asset path. Reports source/imported size, importer and platform overrides, runtime format/storage, color-space and mip settings, bounded per-channel statistics, edge discontinuity, normal validity, and a project semantic-contract classification.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `texture_path` | `str` | yes | Exact texture path under Assets/ or Packages/. |
| `semantic_contract` | `str \| None` | — | Optional contract name such as freshcan_n_ao_r, urp_mask, normal, or color. |
| `sample_size` | `int` | — | Maximum sampled width/height (16-256). |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

