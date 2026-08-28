"""Durable, server-owned validation runs with fail-closed completion."""
from __future__ import annotations

import json
import re
import threading
import time
import uuid
from pathlib import Path
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.state.editor_state_store import editor_state_store
from services.tools import get_unity_instance_from_context
from transport.plugin_hub import PluginHub


_RUN_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$")
_WRITE_LOCK = threading.RLock()
_TERMINAL_NON_PASSING = {
    "failed",
    "blocked",
    "infrastructure_error",
    "no_tests",
    "skipped",
    "aborted",
    "cancelled",
}


def _now_unix_ms() -> int:
    return int(time.time() * 1000)


async def _resolve_project_root(ctx: Context) -> Path:
    unity_instance = await get_unity_instance_from_context(ctx)
    if unity_instance:
        cached = editor_state_store.get_project_root(unity_instance)
        if cached:
            return Path(cached).resolve()

    session_id = await PluginHub._resolve_session_id(unity_instance)
    registry = PluginHub._registry
    session = await registry.get_session(session_id) if registry else None
    if session and session.project_path:
        return Path(session.project_path).resolve()
    raise RuntimeError("Unity project root is unavailable for the selected instance.")


def _run_directory(project_root: Path, run_id: str) -> Path:
    if not _RUN_ID.fullmatch(run_id):
        raise ValueError("run_id contains unsupported characters.")
    return project_root / "Library" / "MCPForUnity" / "ValidationRuns" / run_id


def _read_run(run_directory: Path) -> dict[str, Any]:
    run_path = run_directory / "run.json"
    if not run_path.is_file():
        raise FileNotFoundError("Unknown validation run_id.")
    payload = json.loads(run_path.read_text(encoding="utf-8"))
    if not isinstance(payload, dict):
        raise ValueError("Validation run artifact is invalid.")
    return payload


def _write_atomic(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(payload, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    temporary.replace(path)


def _append_event(run_directory: Path, event: str, data: dict[str, Any]) -> None:
    entry = {
        "timestamp_unix_ms": _now_unix_ms(),
        "event": event,
        "data": data,
    }
    with (run_directory / "timeline.jsonl").open("a", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(entry, sort_keys=True) + "\n")


def _normalize(values: list[str] | str | None) -> list[str]:
    if values is None:
        return []
    candidates = [values] if isinstance(values, str) else values
    return sorted({str(value).strip() for value in candidates if str(value).strip()})


def _completion(run: dict[str, Any]) -> tuple[str, list[str]]:
    expected = set(run.get("expected_checks") or [])
    checks = run.get("checks") or {}
    missing = sorted(expected - set(checks))
    reasons = [f"missing_check:{check_id}" for check_id in missing]

    covered_proof: set[str] = set()
    for check_id in sorted(expected & set(checks)):
        check = checks[check_id]
        outcome = str(check.get("outcome") or "infrastructure_error")
        evidence = check.get("evidence") or []
        if outcome != "passed":
            reasons.append(f"non_passing_check:{check_id}:{outcome}")
        if not evidence:
            reasons.append(f"missing_evidence:{check_id}")
        if outcome == "passed" and evidence:
            covered_proof.update(check.get("proof_levels") or [])

    for proof_level in run.get("required_proof_levels") or []:
        if proof_level not in covered_proof:
            reasons.append(f"missing_proof:{proof_level}")

    if not expected:
        reasons.append("no_expected_checks")
    return ("passed", []) if not reasons else ("failed", reasons)


@mcp_for_unity_tool(
    group="testing",
    unity_target=None,
    description=(
        "Creates and records durable validation runs. Completion is computed fail-closed "
        "from expected checks, proof levels, outcomes, and evidence."
    ),
    annotations=ToolAnnotations(title="Manage Validation", destructiveHint=True),
)
async def manage_validation(
    ctx: Context,
    action: Annotated[Literal["begin", "record", "status", "complete"],
                      "Validation lifecycle action"],
    run_id: Annotated[str | None, "Run id returned by begin"] = None,
    title: Annotated[str | None, "Validation objective"] = None,
    claims: Annotated[list[str] | str | None, "Claims this run is intended to prove"] = None,
    changed_paths: Annotated[list[str] | str | None, "Paths in the implementation scope"] = None,
    expected_checks: Annotated[list[str] | str | None, "Check ids required before completion"] = None,
    required_proof_levels: Annotated[list[str] | str | None,
                                     "Required proof levels such as editmode, playmode, or player"] = None,
    check_id: Annotated[str | None, "Expected check id to record"] = None,
    outcome: Annotated[Literal[
        "passed", "failed", "blocked", "infrastructure_error", "no_tests",
        "skipped", "aborted", "cancelled"] | None, "Observed check outcome"] = None,
    proof_levels: Annotated[list[str] | str | None, "Proof levels supplied by this check"] = None,
    evidence: Annotated[list[str] | str | None, "Concrete result facts or artifact references"] = None,
    artifacts: Annotated[list[str] | str | None, "Artifact paths or identifiers"] = None,
) -> MCPResponse:
    try:
        project_root = await _resolve_project_root(ctx)
        now = _now_unix_ms()
        with _WRITE_LOCK:
            if action == "begin":
                normalized_checks = _normalize(expected_checks)
                if not title or not title.strip():
                    return MCPResponse(success=False, error="title is required for begin")
                if not normalized_checks:
                    return MCPResponse(success=False, error="expected_checks must contain at least one check")
                new_run_id = f"validation-{uuid.uuid4().hex}"
                run_directory = _run_directory(project_root, new_run_id)
                run = {
                    "schema_version": "unity-mcp/validation-run@1",
                    "run_id": new_run_id,
                    "title": title.strip(),
                    "claims": _normalize(claims),
                    "changed_paths": _normalize(changed_paths),
                    "expected_checks": normalized_checks,
                    "required_proof_levels": _normalize(required_proof_levels),
                    "checks": {},
                    "status": "running",
                    "outcome": "running",
                    "validation_passed": False,
                    "started_unix_ms": now,
                    "updated_unix_ms": now,
                    "finished_unix_ms": None,
                    "failure_reasons": [],
                    "artifact_directory": str(run_directory),
                }
                _write_atomic(run_directory / "run.json", run)
                _append_event(run_directory, "began", {"expected_checks": normalized_checks})
                return MCPResponse(success=True, message="Validation run started.", data=run)

            if not run_id:
                return MCPResponse(success=False, error="run_id is required")
            run_directory = _run_directory(project_root, run_id)
            run = _read_run(run_directory)

            if action == "status":
                return MCPResponse(success=True, message="Validation run retrieved.", data=run)

            if action == "record":
                if run.get("status") != "running":
                    return MCPResponse(success=False, error="validation_run_already_completed", data=run)
                if not check_id or check_id not in set(run.get("expected_checks") or []):
                    return MCPResponse(success=False, error="check_id must name an expected check")
                if outcome is None:
                    return MCPResponse(success=False, error="outcome is required for record")
                normalized_evidence = _normalize(evidence)
                check = {
                    "check_id": check_id,
                    "outcome": outcome,
                    "validation_passed": outcome == "passed" and bool(normalized_evidence),
                    "proof_levels": _normalize(proof_levels),
                    "evidence": normalized_evidence,
                    "artifacts": _normalize(artifacts),
                    "recorded_unix_ms": now,
                }
                run.setdefault("checks", {})[check_id] = check
                run["updated_unix_ms"] = now
                _write_atomic(run_directory / "run.json", run)
                _append_event(run_directory, "check_recorded", check)
                return MCPResponse(success=True, message="Validation check recorded.", data=run)

            computed_outcome, reasons = _completion(run)
            run["status"] = computed_outcome
            run["outcome"] = computed_outcome
            run["validation_passed"] = computed_outcome == "passed"
            run["failure_reasons"] = reasons
            run["updated_unix_ms"] = now
            run["finished_unix_ms"] = now
            _write_atomic(run_directory / "run.json", run)
            _append_event(run_directory, "completed", {
                "outcome": computed_outcome,
                "failure_reasons": reasons,
            })
            if computed_outcome in _TERMINAL_NON_PASSING:
                return MCPResponse(success=False, error="validation_failed", data=run)
            return MCPResponse(success=True, message="Validation passed.", data=run)
    except (FileNotFoundError, ValueError) as exc:
        return MCPResponse(success=False, error=str(exc))
    except Exception as exc:
        return MCPResponse(
            success=False,
            error="infrastructure_error",
            message=str(exc),
            hint="retry",
            data={"outcome": "infrastructure_error", "validation_passed": False},
        )
