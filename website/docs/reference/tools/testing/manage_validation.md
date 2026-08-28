---
title: manage_validation
sidebar_label: manage_validation
description: "Creates and records durable validation runs."
---

# `manage_validation`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.manage_validation`

## Description

Creates and records durable validation runs. Completion is computed fail-closed from expected checks, proof levels, outcomes, and evidence.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['begin', 'record', 'status', 'complete']` | yes | Validation lifecycle action |
| `run_id` | `str \| None` | — | Run id returned by begin |
| `title` | `str \| None` | — | Validation objective |
| `claims` | `list[str] \| str \| None` | — | Claims this run is intended to prove |
| `changed_paths` | `list[str] \| str \| None` | — | Paths in the implementation scope |
| `expected_checks` | `list[str] \| str \| None` | — | Check ids required before completion |
| `required_proof_levels` | `list[str] \| str \| None` | — | Required proof levels such as editmode, playmode, or player |
| `check_id` | `str \| None` | — | Expected check id to record |
| `outcome` | `Literal['passed', 'failed', 'blocked', 'infrastructure_error', 'no_tests', 'skipped', 'aborted', 'cancelled'] \| None` | — | Observed check outcome |
| `proof_levels` | `list[str] \| str \| None` | — | Proof levels supplied by this check |
| `evidence` | `list[str] \| str \| None` | — | Concrete result facts or artifact references |
| `artifacts` | `list[str] \| str \| None` | — | Artifact paths or identifiers |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->

