from __future__ import annotations

import pytest

from core.result_budget import apply_result_budget, bounded_json_size
from models.models import MCPResponse
from services.resources.results import get_stored_result
from services.state.result_store import ResultStore, result_store


class _Context:
    def __init__(self, session_id: str, user_id: str | None = None):
        self.session_id = session_id
        self._user_id = user_id

    async def get_state(self, key: str):
        return self._user_id if key == "user_id" else None


@pytest.fixture(autouse=True)
def _clear_result_store():
    result_store.clear()
    yield
    result_store.clear()


@pytest.mark.asyncio
async def test_small_result_stays_inline(monkeypatch):
    monkeypatch.setattr("core.result_budget.INLINE_RESULT_MAX_BYTES", 16_384)
    monkeypatch.setattr(
        "core.result_budget.json.dumps",
        lambda *args, **kwargs: (_ for _ in ()).throw(
            AssertionError("inline JSON should not allocate a serialized copy")
        ),
    )
    result = {"success": True, "data": {"value": "small"}}

    budgeted = await apply_result_budget(
        result,
        tool_name="example",
        ctx=_Context("session-a"),
    )

    assert budgeted is result
    assert result_store.entry_count == 0


@pytest.mark.asyncio
async def test_large_response_becomes_session_scoped_paged_resource(monkeypatch):
    monkeypatch.setattr("core.result_budget.INLINE_RESULT_MAX_BYTES", 16_384)
    original = MCPResponse(success=True, data={"text": "x" * 20_000})
    owner_ctx = _Context("session-a")

    budgeted = await apply_result_budget(
        original,
        tool_name="large_tool",
        ctx=owner_ctx,
    )

    assert isinstance(budgeted, MCPResponse)
    assert budgeted.data["truncated"] is True
    assert budgeted.data["stored"] is True
    result_id = budgeted.data["result_uri"].split("/")[-2]

    page = await get_stored_result(owner_ctx, result_id, 0)
    assert page["success"] is True
    assert page["data"]["tool"] == "large_tool"
    assert len(page["data"]["content"]) == 16_000
    assert page["data"]["next_uri"] is not None

    denied = await get_stored_result(_Context("session-b"), result_id, 0)
    assert denied["success"] is False


@pytest.mark.asyncio
async def test_result_beyond_store_limit_is_rejected_before_full_serialization(
    monkeypatch,
):
    monkeypatch.setattr("core.result_budget.INLINE_RESULT_MAX_BYTES", 16_384)
    monkeypatch.setattr(result_store, "max_item_bytes", 1024)
    original = {
        "success": False,
        "message": "Original failure.",
        "error": "specific_error",
        "hint": "retry",
        "data": {"text": "x" * 20_000},
    }

    budgeted = await apply_result_budget(
        original,
        tool_name="oversized_failure",
        ctx=_Context("session-a"),
    )

    assert budgeted["success"] is False
    assert budgeted["error"] == "specific_error"
    assert budgeted["hint"] == "retry"
    assert budgeted["data"]["stored"] is False
    assert budgeted["data"]["minimum_bytes"] > 1024
    assert "Original failure." in budgeted["message"]


@pytest.mark.asyncio
async def test_escape_heavy_result_is_rejected_before_json_allocation(monkeypatch):
    monkeypatch.setattr("core.result_budget.INLINE_RESULT_MAX_BYTES", 16_384)
    monkeypatch.setattr(result_store, "max_item_bytes", 1024)
    monkeypatch.setattr(
        "core.result_budget.json.dumps",
        lambda *args, **kwargs: (_ for _ in ()).throw(
            AssertionError("oversized JSON should not be serialized")
        ),
    )

    budgeted = await apply_result_budget(
        {"success": True, "data": {"text": "\\" * 800}},
        tool_name="escape_heavy",
        ctx=_Context("session-a"),
    )

    assert budgeted["data"]["stored"] is False
    assert budgeted["data"]["minimum_bytes"] > 1024


def test_result_store_enforces_lru_and_byte_limits():
    store = ResultStore(
        max_entries=2,
        max_total_bytes=10,
        max_item_bytes=8,
        ttl_seconds=30,
    )
    first = store.put("12345", content_type="text/plain", owner="o", tool_name="t")
    second = store.put("67890", content_type="text/plain", owner="o", tool_name="t")
    third = store.put("abcde", content_type="text/plain", owner="o", tool_name="t")

    assert first is not None and second is not None and third is not None
    assert store.get(first, owner="o") is None
    assert store.get(second, owner="o") is not None
    assert store.get(third, owner="o") is not None
    assert store.total_bytes <= 10


def test_result_store_rejects_oversized_item():
    store = ResultStore(max_total_bytes=10, max_item_bytes=4)
    result_id = store.put(
        "12345",
        content_type="text/plain",
        owner="o",
        tool_name="t",
    )
    assert result_id is None


def test_bounded_json_size_stops_before_full_command_serialization(monkeypatch):
    monkeypatch.setattr(
        "core.result_budget.json.dumps",
        lambda *args, **kwargs: (_ for _ in ()).throw(
            AssertionError("large string payload should not be serialized")
        ),
    )

    size_bytes, within_limit = bounded_json_size(
        {"name": "large", "params": {"payload": "x" * 20_000}},
        ceiling=1024,
    )

    assert within_limit is False
    assert size_bytes > 1024
