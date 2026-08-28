---
title: validate_render_contract
sidebar_label: validate_render_contract
description: "Validate the renderer-material-shader-texture closure for an exact material or scene target."
---

# `validate_render_contract`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `rendering_inspect` &nbsp;·&nbsp; **Module:** `services.tools.rendering_workflow`

## Description

Validate the renderer-material-shader-texture closure for an exact material or scene target. Checks bindings, graph reachability, texture/importer semantics, LOD variants, ownership/vendor boundaries, and caller-supplied contracts. Unknown proof fails the strict contract instead of passing green.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `material_path` | `str \| None` | — | Exact material path. |
| `target` | `str \| None` | — | Scene GameObject name, path, or instance ID. |
| `contracts` | `dict[str, Any] \| str \| None` | — | Optional JSON contract overrides keyed by material property or texture path. |
| `strict` | `bool` | — | Treat unknown proof as a validation failure. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

