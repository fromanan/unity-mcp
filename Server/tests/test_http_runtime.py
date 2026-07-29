from __future__ import annotations

import time

from starlette.testclient import TestClient
from fastmcp import FastMCP

from core.config import config
from http_runtime import (
    HttpRuntimeController,
    add_local_control_routes,
    http_runtime_controller,
    shutdown_request_is_authorized,
)
from transport.bounded_streamable_http import create_bounded_streamable_http_app


def test_shutdown_authorization_requires_loopback_and_matching_token():
    assert shutdown_request_is_authorized(
        client_host="127.0.0.1",
        supplied_token="secret",
        expected_token="secret",
    )
    assert shutdown_request_is_authorized(
        client_host="::1",
        supplied_token="secret",
        expected_token="secret",
    )
    assert not shutdown_request_is_authorized(
        client_host="192.168.1.2",
        supplied_token="secret",
        expected_token="secret",
    )
    assert not shutdown_request_is_authorized(
        client_host="127.0.0.1",
        supplied_token="wrong",
        expected_token="secret",
    )
    assert not shutdown_request_is_authorized(
        client_host="127.0.0.1",
        supplied_token=None,
        expected_token="secret",
    )


def test_controller_applies_shutdown_requested_before_attach():
    class FakeServer:
        should_exit = False

    controller = HttpRuntimeController()
    controller.request_shutdown()
    server = FakeServer()
    controller.attach(server, object())
    assert server.should_exit


def test_local_shutdown_route_requires_token_and_requests_exit(monkeypatch):
    class FakeServer:
        should_exit = False

    monkeypatch.setenv("UNITY_MCP_INSTANCE_TOKEN", "secret")
    config.http_remote_hosted = False
    mcp = FastMCP("local-control-test")
    add_local_control_routes(mcp)
    app = create_bounded_streamable_http_app(
        mcp,
        streamable_http_path="/mcp",
        session_idle_timeout=30,
        max_sessions=4,
    )
    fake_server = FakeServer()
    http_runtime_controller.attach(fake_server, app)
    try:
        with TestClient(app, client=("127.0.0.1", 50000)) as client:
            denied = client.post(
                "/api/shutdown",
                headers={"X-Unity-MCP-Instance-Token": "wrong"},
            )
            assert denied.status_code == 404
            assert not fake_server.should_exit

            accepted = client.post(
                "/api/shutdown",
                headers={"X-Unity-MCP-Instance-Token": "secret"},
            )
            assert accepted.status_code == 202
            deadline = time.time() + 1
            while not fake_server.should_exit and time.time() < deadline:
                time.sleep(0.01)
            assert fake_server.should_exit
    finally:
        http_runtime_controller.reset()
