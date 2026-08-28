---
title: manage_tools
sidebar_label: manage_tools
description: "Search and toggle per-session tool groups."
---

# `manage_tools`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_tools`

## Description

Search and toggle per-session tool groups. Actions: list_groups, search, activate, deactivate, sync, reset. Search first and activate only the group needed; sync imports Unity Editor toggle state.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['list_groups', 'search', 'activate', 'deactivate', 'sync', 'reset']` | yes | Action to perform. |
| `group` | `str \| None` | — | Group name for activate or deactivate. |
| `query` | `str \| None` | — | Search text for action=search. Matches group names, descriptions, and tool names. |
| `include_tools` | `bool` | — | Include tool names in list_groups. Defaults false for a compact response. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
Search before expanding the catalog:

```text
manage_tools action=search query="shader graph"
manage_tools action=activate group=rendering_inspect
```

`list_groups` is compact by default. Use `include_tools=true` only when exact tool names are needed.
<!-- examples:end -->

