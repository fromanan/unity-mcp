from __future__ import annotations

import asyncio
import hmac
import ipaddress
import logging
import os
from typing import Any

import uvicorn
from starlette.requests import Request
from starlette.responses import JSONResponse

from transport.bounded_streamable_http import create_bounded_streamable_http_app

logger = logging.getLogger(__name__)


class HttpRuntimeController:
    def __init__(self) -> None:
        self._server: uvicorn.Server | None = None
        self._pending_shutdown = False
        self._app: Any = None

    def attach(self, server: uvicorn.Server, app: Any) -> None:
        self._server = server
        self._app = app
        if self._pending_shutdown:
            server.should_exit = True

    def request_shutdown(self) -> None:
        self._pending_shutdown = True
        if self._server is not None:
            self._server.should_exit = True

    def session_snapshot(self) -> dict[str, int]:
        manager = getattr(getattr(self._app, "state", None), "session_manager", None)
        if manager is None:
            return {
                "active": 0,
                "created": 0,
                "deleted": 0,
                "expired_or_closed": 0,
                "rejected": 0,
                "maximum": 0,
            }
        return manager.snapshot()

    def reset(self) -> None:
        self._server = None
        self._app = None
        self._pending_shutdown = False


http_runtime_controller = HttpRuntimeController()


def is_loopback_client(host: str | None) -> bool:
    if not host:
        return False
    if host.lower() == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def shutdown_request_is_authorized(
    *,
    client_host: str | None,
    supplied_token: str | None,
    expected_token: str | None,
) -> bool:
    return (
        is_loopback_client(client_host)
        and bool(supplied_token)
        and bool(expected_token)
        and hmac.compare_digest(supplied_token, expected_token)
    )


def add_local_control_routes(mcp) -> None:
    """Attach loopback-only, token-authenticated lifecycle endpoints."""

    @mcp.custom_route("/api/server/status", methods=["GET"])
    async def local_server_status(request: Request) -> JSONResponse:
        expected_token = os.environ.get("UNITY_MCP_INSTANCE_TOKEN")
        supplied_token = request.headers.get("X-Unity-MCP-Instance-Token")
        client_host = request.client.host if request.client else None
        if not shutdown_request_is_authorized(
            client_host=client_host,
            supplied_token=supplied_token,
            expected_token=expected_token,
        ):
            return JSONResponse({"error": "Not found"}, status_code=404)
        return JSONResponse({
            "status": "healthy",
            "sessions": http_runtime_controller.session_snapshot(),
        })

    @mcp.custom_route("/api/shutdown", methods=["POST"])
    async def local_server_shutdown(request: Request) -> JSONResponse:
        expected_token = os.environ.get("UNITY_MCP_INSTANCE_TOKEN")
        supplied_token = request.headers.get("X-Unity-MCP-Instance-Token")
        client_host = request.client.host if request.client else None
        if not shutdown_request_is_authorized(
            client_host=client_host,
            supplied_token=supplied_token,
            expected_token=expected_token,
        ):
            return JSONResponse({"error": "Not found"}, status_code=404)
        logger.info("Authenticated local shutdown requested")
        asyncio.get_running_loop().call_soon(
            http_runtime_controller.request_shutdown
        )
        return JSONResponse({"status": "shutting_down"}, status_code=202)


def run_http_server(
    mcp,
    *,
    host: str,
    port: int,
    session_idle_timeout: float,
    max_sessions: int,
    remote_hosted: bool = False,
    allowed_hosts: tuple[str, ...] = (),
    allowed_origins: tuple[str, ...] = (),
) -> None:
    app = create_bounded_streamable_http_app(
        mcp,
        streamable_http_path="/mcp",
        session_idle_timeout=session_idle_timeout,
        max_sessions=max_sessions,
        host_origin_protection=True if remote_hosted else "auto",
        allowed_hosts=list(allowed_hosts) if remote_hosted else None,
        allowed_origins=list(allowed_origins) if remote_hosted else None,
    )
    uvicorn_config = uvicorn.Config(
        app=app,
        host=host,
        port=port,
        log_config=None,
        access_log=False,
    )
    server = uvicorn.Server(uvicorn_config)
    http_runtime_controller.attach(server, app)
    try:
        asyncio.run(server.serve())
    finally:
        http_runtime_controller.reset()
