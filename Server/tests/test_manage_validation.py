import json

import pytest

from .integration.test_helpers import DummyContext


@pytest.mark.asyncio
async def test_validation_completion_fails_closed_for_missing_checks(monkeypatch, tmp_path):
    import services.tools.manage_validation as mod

    async def project_root(_ctx):
        return tmp_path

    monkeypatch.setattr(mod, "_resolve_project_root", project_root)
    started = await mod.manage_validation(
        DummyContext(),
        action="begin",
        title="prove the change",
        expected_checks=["focused", "player"],
        required_proof_levels=["player"],
    )
    run_id = started.data["run_id"]
    recorded = await mod.manage_validation(
        DummyContext(),
        action="record",
        run_id=run_id,
        check_id="focused",
        outcome="passed",
        proof_levels=["editmode"],
        evidence=["3 tests passed"],
    )
    assert recorded.success is True

    completed = await mod.manage_validation(DummyContext(), action="complete", run_id=run_id)
    assert completed.success is False
    assert completed.error == "validation_failed"
    assert "missing_check:player" in completed.data["failure_reasons"]
    assert "missing_proof:player" in completed.data["failure_reasons"]


@pytest.mark.asyncio
async def test_validation_pass_requires_evidence_and_required_proof(monkeypatch, tmp_path):
    import services.tools.manage_validation as mod

    async def project_root(_ctx):
        return tmp_path

    monkeypatch.setattr(mod, "_resolve_project_root", project_root)
    started = await mod.manage_validation(
        DummyContext(),
        action="begin",
        title="player validation",
        expected_checks=["player"],
        required_proof_levels=["player"],
    )
    run_id = started.data["run_id"]
    await mod.manage_validation(
        DummyContext(),
        action="record",
        run_id=run_id,
        check_id="player",
        outcome="passed",
        proof_levels=["player"],
        evidence=["player.log: all assertions passed"],
        artifacts=["player.log"],
    )

    completed = await mod.manage_validation(DummyContext(), action="complete", run_id=run_id)
    assert completed.success is True
    assert completed.data["validation_passed"] is True
    run_path = tmp_path / "Library" / "MCPForUnity" / "ValidationRuns" / run_id / "run.json"
    persisted = json.loads(run_path.read_text(encoding="utf-8"))
    assert persisted["outcome"] == "passed"


@pytest.mark.asyncio
async def test_validation_rejects_passing_check_without_evidence(monkeypatch, tmp_path):
    import services.tools.manage_validation as mod

    async def project_root(_ctx):
        return tmp_path

    monkeypatch.setattr(mod, "_resolve_project_root", project_root)
    started = await mod.manage_validation(
        DummyContext(), action="begin", title="proof", expected_checks=["tests"]
    )
    run_id = started.data["run_id"]
    await mod.manage_validation(
        DummyContext(), action="record", run_id=run_id,
        check_id="tests", outcome="passed", evidence=[],
    )
    completed = await mod.manage_validation(DummyContext(), action="complete", run_id=run_id)
    assert completed.success is False
    assert "missing_evidence:tests" in completed.data["failure_reasons"]


@pytest.mark.asyncio
async def test_preflight_fails_closed_when_editor_state_raises(monkeypatch):
    import services.resources.editor_state as editor_state_mod
    import services.tools.preflight as preflight_mod

    async def unavailable(_ctx):
        raise RuntimeError("transport down")

    monkeypatch.setattr(preflight_mod, "_in_pytest", lambda: False)
    monkeypatch.setattr(editor_state_mod, "get_editor_state", unavailable)
    result = await preflight_mod.preflight(DummyContext())

    assert result.success is False
    assert result.error == "infrastructure_error"
    assert result.data["validation_passed"] is False


@pytest.mark.asyncio
async def test_preflight_fails_closed_for_stale_state(monkeypatch):
    import services.resources.editor_state as editor_state_mod
    import services.tools.preflight as preflight_mod

    async def stale(_ctx):
        return {
            "success": True,
            "data": {
                "advice": {"blocking_reasons": ["stale_status"]},
                "compilation": {"is_compiling": False},
            },
        }

    monkeypatch.setattr(preflight_mod, "_in_pytest", lambda: False)
    monkeypatch.setattr(editor_state_mod, "get_editor_state", stale)
    result = await preflight_mod.preflight(DummyContext())

    assert result.success is False
    assert result.error == "infrastructure_error"
    assert result.data["reason"] == "stale_editor_state"
