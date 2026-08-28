---
title: manage_rendering_authoring
sidebar_label: manage_rendering_authoring
description: "Plan or apply a transactional material, texture-importer, or Shader Graph patch."
---

# `manage_rendering_authoring`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `rendering_authoring` &nbsp;·&nbsp; **Module:** `services.tools.rendering_workflow`

## Description

Plan or apply a transactional material, texture-importer, or Shader Graph patch. Dry-run is the default. Apply requires an expected SHA-256, uses typed/structured operations, enforces project-copy/vendor boundaries, records an exact mutation manifest, imports, waits for editor readiness, and returns semantic post-validation.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `asset_path` | `str` | yes | Exact material, texture, Shader Graph, or Sub Graph path. |
| `asset_kind` | `Literal['material', 'texture_importer', 'shader_graph']` | yes | Patch kind. |
| `operations` | `list[dict[str, Any]] \| str` | yes | Typed operation array or JSON string. Use an empty array to inspect the plan contract. |
| `dry_run` | `bool` | — | Plan without changing files or Unity objects. |
| `expected_sha256` | `str \| None` | — | Required current file SHA-256 for apply; rejects stale plans. |
| `copy_to` | `str \| None` | — | Optional project-owned Assets path copied before patching a vendor asset. |
| `allow_vendor_asset` | `bool` | — | Explicitly allow direct vendor mutation instead of requiring copy_to. |
| `wait_timeout_seconds` | `int` | — | Editor readiness timeout after apply (5-120). |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

