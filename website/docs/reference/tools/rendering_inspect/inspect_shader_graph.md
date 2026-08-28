---
title: inspect_shader_graph
sidebar_label: inspect_shader_graph
description: "Inspect a ShaderLab, Shader Graph, or Sub Graph asset by exact path."
---

# `inspect_shader_graph`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `rendering_inspect` &nbsp;·&nbsp; **Module:** `services.tools.rendering_workflow`

## Description

Inspect a ShaderLab, Shader Graph, or Sub Graph asset by exact path. For graphs, parses concatenated JSON documents without rewriting them and reports targets, blackboard properties, nodes, slots, edges, subgraphs, property-to-output reachability, inert properties, passes, keywords, and compiler messages.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `shader_path` | `str` | yes | Exact .shader, .shadergraph, or .shadersubgraph path. |
| `page_size` | `int` | — | Graph-document summaries per page (1-100). |
| `cursor` | `int` | — | Zero-based graph-document cursor. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

