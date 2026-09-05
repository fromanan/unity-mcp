---
title: run_tests
sidebar_label: run_tests
description: "Starts a Unity test run asynchronously and returns a job_id immediately."
---

# `run_tests`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.run_tests`

## Description

Starts a Unity test run asynchronously and returns a job_id immediately. Poll with get_test_job for progress.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `mode` | `Literal['EditMode', 'PlayMode']` | — | Unity test mode to run |
| `test_names` | `list[str] \| str \| None` | — | Full names of specific tests to run |
| `group_names` | `list[str] \| str \| None` | — | Same as test_names, except it allows for Regex |
| `category_names` | `list[str] \| str \| None` | — | NUnit category names to filter by |
| `assembly_names` | `list[str] \| str \| None` | — | Assembly names to filter tests by |
| `include_failed_tests` | `bool` | — | Include details for failed/skipped tests (default: true) |
| `include_details` | `bool` | — | Include details for all tests (default: false) |
| `init_timeout` | `int \| None` | — | Deprecated compatibility name for init_timeout_ms. Values are milliseconds; 60 means 60ms, not 60 seconds. |
| `init_timeout_ms` | `int \| None` | — | Initialization timeout in milliseconds (1000-600000). Defaults to 15000 for EditMode and 120000 for PlayMode. Use 60000 for 60 seconds. |
| `clear_stuck` | `bool` | — | Clear an orphaned running job instead of starting a run. Use when a job was lost to a domain reload and is blocking every subsequent run. |
| `minimum_tests` | `int` | — | Minimum selected and executed test count required for a pass. |
| `expected_tests` | `list[str] \| str \| None` | — | Exact full test names that must appear in the selected-test manifest. |
| `fail_on_skipped` | `bool` | — | Treat skipped or inconclusive tests as a non-passing outcome. |
| `fidelity` | `Literal['native', 'bridge_preserving']` | — | native preserves Unity's normal Play Mode behavior; bridge_preserving disables domain reload. |
| `allow_scene_save` | `bool` | — | Explicitly allow Unity to save already-saved dirty scenes before the run. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
Use the mode-specific initialization default when no override is required:

```python
job = run_tests(mode="EditMode", test_names=["MyTests.TestSomething"])
result = get_test_job(job_id=job["job_id"], wait_timeout_seconds=60)
```

Timeout units are explicit. For a 60-second initialization allowance, pass `60000` milliseconds:

```python
job = run_tests(mode="PlayMode", init_timeout_ms=120000)
```
<!-- examples:end -->

