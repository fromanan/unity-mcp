---
title: sample_material
sidebar_label: sample_material
description: "Render one exact material in an isolated, deterministic Editor preview."
---

# `sample_material`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `rendering_inspect` &nbsp;·&nbsp; **Module:** `services.tools.rendering_workflow`

## Description

Render one exact material in an isolated, deterministic Editor preview. Selects canonical geometry for PBR, tiled/triplanar, foliage/cutout, or transparent materials; supports clone-only typed property overrides and a locked side-by-side comparison. Returns a bounded PNG contact sheet, exact dependency/preview manifest, material inspection, context warnings, cache evidence, and restoration proof. This is a fast authoring sample, not scene, Player, or target-GPU truth.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `material_path` | `str` | yes | Exact material path under Assets/ or Packages/. |
| `profile` | `Literal['auto', 'pbr', 'tiled', 'foliage', 'transparent']` | — | Canonical preview profile; auto classifies the material and shader. |
| `compare_to_material_path` | `str \| None` | — | Optional exact material path rendered beside the primary with the same views. |
| `property_overrides` | `dict[str, Any] \| str \| None` | — | Typed property overrides applied only to temporary material clones. |
| `max_resolution` | `int` | — | Maximum contact-sheet width/height (256-512). |
| `warmup_frames` | `int` | — | Preview renders before each captured panel (0-4). |
| `include_image` | `bool` | — | Include the contact-sheet PNG as base64. |
| `output_path` | `str \| None` | — | Optional .png path under Library/MCPForUnity/MaterialSamples/. |
| `cache_mode` | `Literal['use', 'refresh', 'bypass']` | — | Use a valid cached sample, refresh it, or bypass cache reads and writes. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

