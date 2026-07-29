from __future__ import annotations

import time

from fastmcp import FastMCP
from starlette.testclient import TestClient

from transport.bounded_streamable_http import create_bounded_streamable_http_app


INITIALIZE = {
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
        "protocolVersion": "2025-03-26",
        "capabilities": {},
        "clientInfo": {"name": "test", "version": "1"},
    },
}
HEADERS = {
    "Accept": "application/json, text/event-stream",
    "Content-Type": "application/json",
}


def _app(*, timeout: float = 30, maximum: int = 1):
    return create_bounded_streamable_http_app(
        FastMCP("bounded-test"),
        streamable_http_path="/mcp",
        session_idle_timeout=timeout,
        max_sessions=maximum,
    )


def test_rejects_new_session_at_limit_and_allows_after_delete():
    app = _app(maximum=1)
    with TestClient(app) as client:
        first = client.post("/mcp", headers=HEADERS, json=INITIALIZE)
        assert first.status_code == 200
        session_id = first.headers["mcp-session-id"]

        rejected = client.post("/mcp", headers=HEADERS, json=INITIALIZE)
        assert rejected.status_code == 503
        assert rejected.headers["retry-after"] == "5"

        deleted = client.delete(
            "/mcp",
            headers={**HEADERS, "mcp-session-id": session_id},
        )
        assert deleted.status_code == 200

        replacement = client.post("/mcp", headers=HEADERS, json=INITIALIZE)
        assert replacement.status_code == 200
        snapshot = app.state.session_manager.snapshot()
        assert snapshot == {
            "active": 1,
            "created": 2,
            "deleted": 1,
            "expired_or_closed": 0,
            "rejected": 1,
            "maximum": 1,
        }


def test_idle_session_expires_and_old_id_returns_404():
    app = _app(timeout=0.05, maximum=1)
    with TestClient(app) as client:
        initialized = client.post("/mcp", headers=HEADERS, json=INITIALIZE)
        session_id = initialized.headers["mcp-session-id"]
        time.sleep(0.2)

        expired = client.post(
            "/mcp",
            headers={**HEADERS, "mcp-session-id": session_id},
            json={"jsonrpc": "2.0", "id": 2, "method": "ping"},
        )
        assert expired.status_code == 404
        snapshot = app.state.session_manager.snapshot()
        assert snapshot["active"] == 0
        assert snapshot["expired_or_closed"] == 1
