from __future__ import annotations

import logging
from collections.abc import AsyncGenerator
from contextlib import asynccontextmanager
from dataclasses import dataclass
from http import HTTPStatus
from typing import Any

import anyio
import fastmcp
from fastmcp.server.http import (
    FastMCPStreamableHTTPSessionManager,
    HostOriginGuardMiddleware,
    RequireAuthMiddleware,
    StreamableHTTPASGIApp,
    build_resource_metadata_url,
    create_base_app,
)
from mcp.server.streamable_http import MCP_SESSION_ID_HEADER, EventStore
from starlette.middleware import Middleware
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import BaseRoute, Route

logger = logging.getLogger(__name__)


@dataclass
class SessionMetrics:
    created: int = 0
    deleted: int = 0
    expired_or_closed: int = 0
    rejected: int = 0


class BoundedSessionManager(FastMCPStreamableHTTPSessionManager):
    """FastMCP session manager with idle expiry, admission limits, and cleanup."""

    def __init__(
        self,
        *args: Any,
        session_idle_timeout: float,
        max_sessions: int,
        metrics: SessionMetrics,
        **kwargs: Any,
    ) -> None:
        if session_idle_timeout <= 0:
            raise ValueError("session_idle_timeout must be positive")
        if max_sessions <= 0:
            raise ValueError("max_sessions must be positive")
        super().__init__(*args, **kwargs)
        # FastMCP 3.4.x does not expose the MCP SDK's idle-timeout parameter.
        # The underlying SDK reads this attribute when creating each session.
        self.session_idle_timeout = session_idle_timeout
        self.max_sessions = max_sessions
        self.metrics = metrics
        self._admission_lock = anyio.Lock()
        self._known_sessions: set[str] = set()

    def _prune_terminated(self) -> None:
        for session_id, transport in list(self._server_instances.items()):
            if not transport.is_terminated:
                continue
            self._server_instances.pop(session_id, None)
            owners = getattr(self, "_session_owners", None)
            if owners is not None:
                owners.pop(session_id, None)
            if session_id in self._known_sessions:
                self._known_sessions.remove(session_id)
                self.metrics.expired_or_closed += 1

    def snapshot(self) -> dict[str, int]:
        self._prune_terminated()
        active = set(self._server_instances)
        removed = self._known_sessions - active
        if removed:
            self.metrics.expired_or_closed += len(removed)
            self._known_sessions.difference_update(removed)
        self._known_sessions.update(active)
        return {
            "active": len(active),
            "created": self.metrics.created,
            "deleted": self.metrics.deleted,
            "expired_or_closed": self.metrics.expired_or_closed,
            "rejected": self.metrics.rejected,
            "maximum": self.max_sessions,
        }

    async def _handle_stateful_request(self, scope, receive, send) -> None:
        request = Request(scope, receive)
        session_id = request.headers.get(MCP_SESSION_ID_HEADER)
        method = request.method.upper()

        if session_id is None:
            async with self._admission_lock:
                self._prune_terminated()
                if len(self._server_instances) >= self.max_sessions:
                    self.metrics.rejected += 1
                    response = JSONResponse(
                        {
                            "error": "MCP session limit reached",
                            "active_sessions": len(self._server_instances),
                            "max_sessions": self.max_sessions,
                        },
                        status_code=HTTPStatus.SERVICE_UNAVAILABLE,
                        headers={"Retry-After": "5"},
                    )
                    await response(scope, receive, send)
                    return

                before = set(self._server_instances)
                await super()._handle_stateful_request(scope, receive, send)
                created = set(self._server_instances) - before
                if created:
                    self.metrics.created += len(created)
                    self._known_sessions.update(created)
                return

        await super()._handle_stateful_request(scope, receive, send)
        if method == "DELETE":
            transport = self._server_instances.pop(session_id, None)
            owners = getattr(self, "_session_owners", None)
            if owners is not None:
                owners.pop(session_id, None)
            if transport is not None:
                self.metrics.deleted += 1
                self._known_sessions.discard(session_id)
                if not transport.is_terminated:
                    await transport.terminate()
        else:
            self._prune_terminated()


def create_bounded_streamable_http_app(
    server,
    *,
    streamable_http_path: str,
    session_idle_timeout: float,
    max_sessions: int,
    event_store: EventStore | None = None,
    retry_interval: int | None = None,
    json_response: bool = False,
    stateless_http: bool = False,
    routes: list[BaseRoute] | None = None,
    middleware: list[Middleware] | None = None,
    host_origin_protection: bool | str = "auto",
    allowed_hosts: list[str] | None = None,
    allowed_origins: list[str] | None = None,
):
    """Build FastMCP's HTTP app while supplying bounded SDK session settings."""

    server_routes: list[BaseRoute] = []
    server_middleware: list[Middleware] = []
    metrics = SessionMetrics()
    streamable_http_app = StreamableHTTPASGIApp(None)

    if server.auth:
        server_middleware.extend(server.auth.get_middleware())
        server_routes.extend(server.auth.get_routes(mcp_path=streamable_http_path))
        resource_url = server.auth._get_resource_url(streamable_http_path)
        resource_metadata_url = (
            build_resource_metadata_url(resource_url) if resource_url else None
        )
        methods = ["POST", "DELETE"] if stateless_http else ["GET", "POST", "DELETE"]
        server_routes.append(
            Route(
                streamable_http_path,
                endpoint=RequireAuthMiddleware(
                    streamable_http_app,
                    server.auth.required_scopes,
                    resource_metadata_url,
                ),
                methods=methods,
            )
        )
    else:
        methods = ["POST", "DELETE"] if stateless_http else None
        server_routes.append(
            Route(streamable_http_path, endpoint=streamable_http_app, methods=methods)
        )

    if routes:
        server_routes.extend(routes)
    server_routes.extend(server._get_additional_http_routes())
    if host_origin_protection not in (True, False, "auto"):
        raise ValueError(
            "host_origin_protection must be True, False, or 'auto'"
        )
    if host_origin_protection is not False:
        server_middleware.insert(
            0,
            Middleware(
                HostOriginGuardMiddleware,
                allowed_hosts=allowed_hosts,
                allowed_origins=allowed_origins,
                mode="strict" if host_origin_protection is True else "auto",
            ),
        )
    if middleware:
        server_middleware.extend(middleware)

    @asynccontextmanager
    async def lifespan(_) -> AsyncGenerator[None, None]:
        manager = BoundedSessionManager(
            app=server._mcp_server,
            event_store=event_store,
            retry_interval=retry_interval,
            json_response=json_response,
            stateless=stateless_http,
            session_idle_timeout=session_idle_timeout,
            max_sessions=max_sessions,
            metrics=metrics,
        )
        streamable_http_app.session_manager = manager
        app.state.session_manager = manager
        async with server._lifespan_manager(), manager.run():
            try:
                yield
            finally:
                for transport in list(manager._server_instances.values()):
                    try:
                        await transport.terminate()
                    except Exception:
                        logger.debug(
                            "Error terminating streamable HTTP transport",
                            exc_info=True,
                        )

    app = create_base_app(
        routes=server_routes,
        middleware=server_middleware,
        debug=fastmcp.settings.debug,
        lifespan=lifespan,
    )
    app.state.fastmcp_server = server
    app.state.path = streamable_http_path
    app.state.transport_type = "streamable-http"
    app.state.session_metrics = metrics
    app.state.session_manager = None
    return app
