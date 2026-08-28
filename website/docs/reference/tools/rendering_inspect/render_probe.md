---
title: render_probe
sidebar_label: render_probe
description: "Capture a deterministic color or wireframe render probe from an existing camera."
---

# `render_probe`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `rendering_inspect` &nbsp;·&nbsp; **Module:** `services.tools.rendering_workflow`

## Description

Capture a deterministic color or wireframe render probe from an existing camera. Locks width, height, quality, camera state, warmup count, output path, and restoration evidence; rejects unsupported debug channels.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `camera` | `str \| None` | — | Camera GameObject name, path, or instance ID. |
| `target` | `str \| None` | — | Optional target recorded with the capture manifest. |
| `scope` | `Literal['scene', 'target']` | — | Capture the full camera scene or an isolated target preview. |
| `output_path` | `str \| None` | — | Project-relative output path; defaults under Library/MCPForUnity/RenderProbes. |
| `width` | `int` | — | Capture width (64-4096). |
| `height` | `int` | — | Capture height (64-4096). |
| `channel` | `Literal['color', 'wireframe']` | — | Supported capture channel. |
| `warmup_frames` | `int` | — | Synchronous camera renders before capture (0-8). |
| `quality_level` | `int \| None` | — | Optional quality-level index, restored afterward. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

