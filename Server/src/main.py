from starlette.requests import Request
from transport.unity_instance_middleware import (
    UnityInstanceMiddleware,
    get_unity_instance_middleware
)
from services.api_key_service import ApiKeyService
from transport.legacy.unity_connection import get_unity_connection_pool, UnityConnectionPool
from services.tools import register_all_tools
from core.telemetry import record_milestone, record_telemetry, MilestoneType, RecordType, get_package_version, shutdown_telemetry
from services.resources import register_all_resources
from transport.plugin_registry import PluginRegistry
from transport.plugin_hub import PluginHub
from core.logging_config import configure_server_logging
from services.custom_tool_service import (
    CustomToolService,
    resolve_project_id_for_unity_instance,
)
from core.config import config
from http_runtime import (
    add_local_control_routes,
    http_runtime_controller,
    run_http_server,
)
from starlette.routing import WebSocketRoute
from starlette.responses import JSONResponse
import argparse
import asyncio

# Fix to IPV4 Connection Issue #853
# Will disable features in ProactorEventLoop including subprocess pipes and named pipes
import sys
if sys.platform == "win32":
    asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

import logging
from contextlib import asynccontextmanager
import os
import time
from typing import AsyncIterator, Any
from urllib.parse import urlparse

from fastmcp import FastMCP


# stdout is reserved for MCP stdio protocol frames. The listener owns all
# formatting and local I/O through stderr and the rotating server log.
_logging_runtime = configure_server_logging(config)
logger = logging.getLogger("mcp-for-unity-server")

# Import telemetry only after logging is configured to ensure its logs use stderr and proper levels
# Ensure a slightly higher telemetry timeout unless explicitly overridden by env
try:

    # Ensure generous timeout unless explicitly overridden by env
    if not os.environ.get("UNITY_MCP_TELEMETRY_TIMEOUT"):
        os.environ["UNITY_MCP_TELEMETRY_TIMEOUT"] = "5.0"
except Exception:
    pass

# Global connection pool
_unity_connection_pool: UnityConnectionPool | None = None
_plugin_registry: PluginRegistry | None = None

# Cached server version (set at startup to avoid repeated I/O)
_server_version: str | None = None

# In-memory custom tool service initialized after MCP construction
custom_tool_service: CustomToolService | None = None


@asynccontextmanager
async def server_lifespan(server: FastMCP) -> AsyncIterator[dict[str, Any]]:
    """Handle server startup and shutdown."""
    global _unity_connection_pool, _server_version
    _server_version = get_package_version()
    logger.info("MCP for Unity Server v%s starting up", _server_version)

    # Register custom tool management endpoints with FastMCP
    # Routes are declared globally below after FastMCP initialization

    # Note: When using HTTP transport, FastMCP handles the HTTP server
    # Tool registration will be handled through FastMCP endpoints
    enable_http_server = os.environ.get(
        "UNITY_MCP_ENABLE_HTTP_SERVER", "").lower() in ("1", "true", "yes", "on")
    if enable_http_server:
        http_host = os.environ.get("UNITY_MCP_HTTP_HOST", "localhost")
        http_port = int(os.environ.get("UNITY_MCP_HTTP_PORT", "8080"))
        logger.info(
            f"HTTP tool registry will be available on http://{http_host}:{http_port}")

    global _plugin_registry
    _plugin_registry = PluginRegistry()
    loop = asyncio.get_running_loop()
    PluginHub.configure(_plugin_registry, loop, mcp=server)

    # Record server startup telemetry
    start_time = time.time()
    start_clk = time.perf_counter()
    deferred_tasks: set[asyncio.Task] = set()

    async def _run_deferred(callback) -> None:
        await asyncio.sleep(1.0)
        try:
            await asyncio.to_thread(callback)
        except asyncio.CancelledError:
            raise
        except Exception:
            logger.debug("Deferred telemetry failed", exc_info=True)

    def schedule_deferred(callback) -> None:
        task = asyncio.create_task(_run_deferred(callback))
        deferred_tasks.add(task)
        task.add_done_callback(deferred_tasks.discard)

    # Defer initial telemetry by 1s to avoid stdio handshake interference

    def _emit_startup():
        try:
            record_telemetry(RecordType.STARTUP, {
                "server_version": _server_version,
                "startup_time": start_time,
            })
            record_milestone(MilestoneType.FIRST_STARTUP)
        except Exception:
            logger.debug("Deferred startup telemetry failed", exc_info=True)
    schedule_deferred(_emit_startup)

    try:
        skip_connect = os.environ.get(
            "UNITY_MCP_SKIP_STARTUP_CONNECT", "").lower() in ("1", "true", "yes", "on")
        if skip_connect:
            logger.info(
                "Skipping Unity connection on startup (UNITY_MCP_SKIP_STARTUP_CONNECT=1)")
        else:
            # Initialize connection pool and discover instances
            _unity_connection_pool = get_unity_connection_pool()
            instances = _unity_connection_pool.discover_all_instances()

            if instances:
                logger.info(
                    f"Discovered {len(instances)} Unity instance(s): {[i.id for i in instances]}")

                # Try to connect to default instance
                try:
                    _unity_connection_pool.get_connection()
                    logger.info(
                        "Connected to default Unity instance on startup")

                    # In stdio mode, query Unity for tool enabled states and sync
                    # server-level visibility. In HTTP mode this is handled by
                    # register_tools via WebSocket in PluginHub.
                    if (config.transport_mode or "stdio").lower() != "http":
                        try:
                            from services.tools import sync_tool_visibility_from_unity
                            sync_result = await sync_tool_visibility_from_unity(notify=False)
                            if sync_result.get("synced"):
                                logger.info(
                                    "Stdio startup: synced tool visibility from Unity — "
                                    "enabled=[%s], disabled=[%s]",
                                    ", ".join(sync_result.get("enabled_groups", [])),
                                    ", ".join(sync_result.get("disabled_groups", [])),
                                )
                            else:
                                # Unsupported command = old Unity package; just debug-log
                                log_fn = logger.debug if sync_result.get("unsupported") else logger.warning
                                log_fn(
                                    "Stdio startup: could not sync tool visibility: %s",
                                    sync_result.get("error", "unknown"),
                                )
                        except Exception as sync_exc:
                            logger.debug(
                                "Stdio startup: tool visibility sync failed: %s", sync_exc)

                    # Record successful Unity connection (deferred)
                    schedule_deferred(lambda: record_telemetry(
                        RecordType.UNITY_CONNECTION,
                        {
                            "status": "connected",
                            "connection_time_ms": (time.perf_counter() - start_clk) * 1000,
                            "instance_count": len(instances)
                        }
                    ))
                except Exception as e:
                    logger.warning(
                        f"Could not connect to default Unity instance: {e}")
            else:
                logger.warning("No Unity instances found on startup")

    except ConnectionError as e:
        logger.warning(f"Could not connect to Unity on startup: {e}")

        # Record connection failure (deferred)
        _err_msg = str(e)[:200]
        schedule_deferred(lambda: record_telemetry(
            RecordType.UNITY_CONNECTION,
            {
                "status": "failed",
                "error": _err_msg,
                "connection_time_ms": (time.perf_counter() - start_clk) * 1000,
            }
        ))
    except Exception as e:
        logger.warning(f"Unexpected error connecting to Unity on startup: {e}")
        _err_msg = str(e)[:200]
        schedule_deferred(lambda: record_telemetry(
            RecordType.UNITY_CONNECTION,
            {
                "status": "failed",
                "error": _err_msg,
                "connection_time_ms": (time.perf_counter() - start_clk) * 1000,
            }
        ))

    try:
        # Yield shared state for lifespan consumers (e.g., middleware)
        yield {
            "pool": _unity_connection_pool,
            "plugin_registry": _plugin_registry,
        }
    finally:
        for task in deferred_tasks:
            task.cancel()
        if deferred_tasks:
            await asyncio.gather(*deferred_tasks, return_exceptions=True)
        await PluginHub.shutdown()
        await ApiKeyService.close_instance()
        from services.tools.unity_docs import close_unity_docs_http_client
        await close_unity_docs_http_client()
        from services.state.result_store import result_store
        result_store.clear()
        shutdown_telemetry()
        _plugin_registry = None
        if _unity_connection_pool:
            _unity_connection_pool.disconnect_all()
        logger.info("MCP for Unity Server shut down")


def _build_instructions(project_scoped_tools: bool) -> str:
    custom_tools_note = (
        "Read mcpforunity://custom-tools for active-project extensions."
        if project_scoped_tools
        else "Connected custom tools may appear as standard tools."
    )
    return f"""Control the Unity Editor through resources (read state) and tools (act).
{custom_tools_note}

- Start with the small bootstrap catalog. Use manage_tools(search) then activate only the group needed.
- Read exact resource URIs from resources/list; payload fields are under data.
- If multiple Editors are connected, read mcpforunity://instances and call set_active_instance(Name@hash). A per-call unity_instance overrides routing without changing the session default.
- Use project-relative Assets/... paths with forward slashes.
- Prefer summary-first, paged calls. Follow returned cursors/URIs; avoid previews, stack traces, component properties, or exact totals until needed.
- After script changes, wait for compilation/domain reload and read filtered console errors before using new types.
- For API uncertainty, activate docs and verify with unity_reflect, then unity_docs.
- Rendering: inspect the real owner/material/texture/shader contract first. Activate rendering_authoring only for an authorized mutation and dry-run it before apply.
- Oversized results return a paged mcpforunity://results/... URI instead of bloating tool context.
"""


def _normalize_instance_token(instance_token: str | None) -> tuple[str | None, str | None]:
    if not instance_token:
        return None, None
    if "@" in instance_token:
        name_part, _, hash_part = instance_token.partition("@")
        return (name_part or None), (hash_part or None)
    return None, instance_token


def create_mcp_server(project_scoped_tools: bool) -> FastMCP:
    mcp = FastMCP(
        name="mcp-for-unity-server",
        version=get_package_version(),
        lifespan=server_lifespan,
        instructions=_build_instructions(project_scoped_tools),
    )

    global custom_tool_service
    custom_tool_service = CustomToolService(
        mcp, project_scoped_tools=project_scoped_tools)

    @mcp.custom_route("/health", methods=["GET"])
    async def health_http(_: Request) -> JSONResponse:
        return JSONResponse({
            "status": "healthy",
            "timestamp": time.time(),
            "version": _server_version or "unknown",
            "message": "MCP for Unity server is running"
        })

    if not config.http_remote_hosted:
        add_local_control_routes(mcp)

    @mcp.custom_route("/api/auth/login-url", methods=["GET"])
    async def auth_login_url(_: Request) -> JSONResponse:
        """Return the login URL for users to obtain/manage API keys."""
        if not config.api_key_login_url:
            return JSONResponse(
                {
                    "success": False,
                    "error": "API key management not configured. Contact your server administrator.",
                },
                status_code=404,
            )
        return JSONResponse({
            "success": True,
            "login_url": config.api_key_login_url,
        })

    # Only expose CLI routes if running locally (not in remote hosted mode)
    if not config.http_remote_hosted:
        @mcp.custom_route("/api/command", methods=["POST"])
        async def cli_command_route(request: Request) -> JSONResponse:
            """REST endpoint for CLI commands to Unity."""
            try:
                body = await request.json()

                command_type = body.get("type")
                params = body.get("params", {})
                unity_instance = body.get("unity_instance")

                if not command_type:
                    return JSONResponse({"success": False, "error": "Missing 'type' field"}, status_code=400)

                # Get available sessions
                sessions = await PluginHub.get_sessions()
                if not sessions.sessions:
                    return JSONResponse({
                        "success": False,
                        "error": "No Unity instances connected. Make sure Unity is running with MCP plugin."
                    }, status_code=503)

                # Find target session
                session_id = None
                session_details = None
                instance_name, instance_hash = _normalize_instance_token(
                    unity_instance)
                if unity_instance:
                    # Try to match by hash or project name
                    for sid, details in sessions.sessions.items():
                        if details.hash == instance_hash or details.project in (instance_name, unity_instance):
                            session_id = sid
                            session_details = details
                            break

                # If a specific unity_instance was requested but not found, return an error
                # (Check done here so execute_custom_tool can also validate the instance)
                if unity_instance and not session_id:
                    return JSONResponse(
                        {
                            "success": False,
                            "error": f"Unity instance '{unity_instance}' not found",
                        },
                        status_code=404,
                    )

                # If no specific unity_instance requested, use first available session
                # (Must be done before execute_custom_tool check so all command types benefit)
                if not session_id:
                    try:
                        session_id = next(iter(sessions.sessions.keys()))
                        session_details = sessions.sessions.get(session_id)
                    except StopIteration:
                        # No sessions available - sessions.sessions is empty
                        # This should not happen since we checked at line 378, but handle gracefully
                        return JSONResponse({
                            "success": False,
                            "error": "No Unity instances connected. Make sure Unity is running with MCP plugin."
                        }, status_code=503)

                # Custom tool execution - must be checked BEFORE the final PluginHub.send_command call
                # This applies to both cases: with or without explicit unity_instance
                if command_type == "execute_custom_tool":
                    # session_id and session_details are already set above
                    if not session_id or not session_details:
                        return JSONResponse(
                            {"success": False,
                                "error": "No valid Unity session available for custom tool execution"},
                            status_code=503,
                        )
                    tool_name = None
                    tool_params = {}
                    if isinstance(params, dict):
                        tool_name = params.get(
                            "tool_name") or params.get("name")
                        tool_params = params.get(
                            "parameters") or params.get("params") or {}

                    if not tool_name:
                        return JSONResponse(
                            {"success": False,
                                "error": "Missing 'tool_name' for execute_custom_tool"},
                            status_code=400,
                        )
                    if tool_params is None:
                        tool_params = {}
                    if not isinstance(tool_params, dict):
                        return JSONResponse(
                            {"success": False,
                                "error": "Tool parameters must be an object/dict"},
                            status_code=400,
                        )

                    # Prefer a concrete hash for project-scoped tools.
                    unity_instance_hint = unity_instance
                    if session_details and session_details.hash:
                        unity_instance_hint = session_details.hash

                    project_id = resolve_project_id_for_unity_instance(
                        unity_instance_hint)
                    if not project_id:
                        return JSONResponse(
                            {"success": False,
                                "error": "Could not resolve project id for custom tool"},
                            status_code=400,
                        )

                    service = CustomToolService.get_instance()
                    result = await service.execute_tool(
                        project_id, tool_name, unity_instance_hint, tool_params
                    )
                    return JSONResponse(result.model_dump())

                # Send command to Unity
                result = await PluginHub.send_command(session_id, command_type, params)
                return JSONResponse(result)

            except Exception as e:
                logger.exception("CLI command error: %s", e)
                return JSONResponse({"success": False, "error": str(e)}, status_code=500)

        @mcp.custom_route("/api/instances", methods=["GET"])
        async def cli_instances_route(_: Request) -> JSONResponse:
            """REST endpoint to list connected Unity instances."""
            try:
                sessions = await PluginHub.get_sessions()
                instances = []
                for session_id, details in sessions.sessions.items():
                    instances.append({
                        "session_id": session_id,
                        "project": details.project,
                        "hash": details.hash,
                        "unity_version": details.unity_version,
                        "connected_at": details.connected_at,
                    })
                return JSONResponse({"success": True, "instances": instances})
            except Exception as e:
                return JSONResponse({"success": False, "error": str(e)}, status_code=500)

        @mcp.custom_route("/api/custom-tools", methods=["GET"])
        async def cli_custom_tools_route(request: Request) -> JSONResponse:
            """REST endpoint to list custom tools for the active Unity project."""
            try:
                unity_instance = request.query_params.get("instance")
                instance_name, instance_hash = _normalize_instance_token(
                    unity_instance)

                sessions = await PluginHub.get_sessions()
                if not sessions.sessions:
                    return JSONResponse({
                        "success": False,
                        "error": "No Unity instances connected. Make sure Unity is running with MCP plugin."
                    }, status_code=503)

                session_details = None
                if unity_instance:
                    # Try to match by hash or project name
                    for _, details in sessions.sessions.items():
                        if details.hash == instance_hash or details.project in (instance_name, unity_instance):
                            session_details = details
                            break
                    if not session_details:
                        return JSONResponse(
                            {
                                "success": False,
                                "error": f"Unity instance '{unity_instance}' not found",
                            },
                            status_code=404,
                        )
                else:
                    # No specific unity_instance requested: use first available session
                    session_details = next(iter(sessions.sessions.values()))

                unity_instance_hint = unity_instance
                if session_details and session_details.hash:
                    unity_instance_hint = session_details.hash

                project_id = resolve_project_id_for_unity_instance(
                    unity_instance_hint)
                if not project_id:
                    return JSONResponse(
                        {"success": False,
                            "error": "Could not resolve project id for custom tools"},
                        status_code=400,
                    )

                service = CustomToolService.get_instance()
                tools = await service.list_registered_tools(project_id)
                tools_payload = [
                    tool.model_dump() if hasattr(tool, "model_dump") else tool for tool in tools
                ]

                return JSONResponse({
                    "success": True,
                    "project_id": project_id,
                    "tool_count": len(tools_payload),
                    "tools": tools_payload,
                })
            except Exception as e:
                logger.exception("CLI custom tools error: %s", e)
                return JSONResponse({"success": False, "error": str(e)}, status_code=500)

    # Initialize and register middleware for session-based Unity instance routing
    # Using the singleton getter ensures we use the same instance everywhere
    unity_middleware = get_unity_instance_middleware()
    mcp.add_middleware(unity_middleware)
    logger.info("Registered Unity instance middleware for session-based routing")

    # Initialize API key authentication if in remote-hosted mode
    if config.http_remote_hosted and config.api_key_validation_url:
        ApiKeyService(
            validation_url=config.api_key_validation_url,
            cache_ttl=config.api_key_cache_ttl,
            cache_max_entries=config.api_key_cache_max_entries,
            service_token_header=config.api_key_service_token_header,
            service_token=config.api_key_service_token,
        )
        logger.info(
            "Initialized API key authentication service (validation URL: %s, TTL: %.0fs)",
            config.api_key_validation_url,
            config.api_key_cache_ttl,
        )

    # Mount plugin websocket hub at /hub/plugin when HTTP transport is active.
    # NOTE: Uses FastMCP private API because custom_route() only supports HTTP
    # methods, not WebSocket. _additional_http_routes accepts Starlette Route
    # objects and is still present in FastMCP 3.x.
    existing_routes = [
        route for route in mcp._get_additional_http_routes()
        if isinstance(route, WebSocketRoute) and route.path == "/hub/plugin"
    ]
    if not existing_routes:
        mcp._additional_http_routes.append(
            WebSocketRoute("/hub/plugin", PluginHub))

    # Register all tools
    register_all_tools(mcp, project_scoped_tools=project_scoped_tools)

    # Register all resources
    register_all_resources(mcp, project_scoped_tools=project_scoped_tools)

    return mcp


def main():
    """Entry point for uvx and console scripts."""
    parser = argparse.ArgumentParser(
        description="MCP for Unity Server",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Environment Variables:
  UNITY_MCP_DEFAULT_INSTANCE   Default Unity instance to target (project name, hash, or 'Name@hash')
  UNITY_MCP_SKIP_STARTUP_CONNECT   Skip initial Unity connection attempt (set to 1/true/yes/on)
  UNITY_MCP_TELEMETRY_ENABLED   Enable telemetry (set to 1/true/yes/on)
  UNITY_MCP_TRANSPORT   Transport protocol: stdio or http (default: stdio)
  UNITY_MCP_HTTP_URL   HTTP server URL (default: http://127.0.0.1:8080)
  UNITY_MCP_HTTP_HOST   HTTP server host (overrides URL host)
  UNITY_MCP_HTTP_PORT   HTTP server port (overrides URL port)

Examples:
  # Use specific Unity project as default
  python -m src.server --default-instance "MyProject"

  # Start with HTTP transport
  python -m src.server --transport http --http-url http://127.0.0.1:8080

  # Start with stdio transport (default)
  python -m src.server --transport stdio

  # Use environment variable for transport
  UNITY_MCP_TRANSPORT=http UNITY_MCP_HTTP_URL=http://localhost:9000 python -m src.server
        """
    )
    parser.add_argument(
        "--default-instance",
        type=str,
        metavar="INSTANCE",
        help="Default Unity instance to target (project name, hash, or 'Name@hash'). "
             "Overrides UNITY_MCP_DEFAULT_INSTANCE environment variable."
    )
    parser.add_argument(
        "--transport",
        type=str,
        choices=["stdio", "http"],
        default="stdio",
        help="Transport protocol to use: stdio or http (default: stdio). "
             "Overrides UNITY_MCP_TRANSPORT environment variable."
    )
    parser.add_argument(
        "--http-url",
        type=str,
        default="http://127.0.0.1:8080",
        metavar="URL",
        help="HTTP server URL (default: http://127.0.0.1:8080). "
             "Can also set via UNITY_MCP_HTTP_URL environment variable."
    )
    parser.add_argument(
        "--http-host",
        type=str,
        default=None,
        metavar="HOST",
        help="HTTP server host (overrides URL host). "
             "Overrides UNITY_MCP_HTTP_HOST environment variable."
    )
    parser.add_argument(
        "--http-port",
        type=int,
        default=None,
        metavar="PORT",
        help="HTTP server port (overrides URL port). "
             "Overrides UNITY_MCP_HTTP_PORT environment variable."
    )
    parser.add_argument(
        "--http-remote-hosted",
        action="store_true",
        help="Treat HTTP transport as remotely hosted (forces explicit Unity instance selection). "
             "Can also set via UNITY_MCP_HTTP_REMOTE_HOSTED=true."
    )
    parser.add_argument(
        "--http-session-idle-timeout",
        type=float,
        default=300.0,
        metavar="SECONDS",
        help="Expire inactive Streamable HTTP sessions after this many seconds "
             "(default: 300). Can also set via "
             "UNITY_MCP_HTTP_SESSION_IDLE_TIMEOUT."
    )
    parser.add_argument(
        "--http-max-sessions",
        type=int,
        default=16,
        metavar="COUNT",
        help="Maximum concurrent Streamable HTTP sessions (default: 16). "
             "Can also set via UNITY_MCP_HTTP_MAX_SESSIONS."
    )
    parser.add_argument(
        "--http-allowed-host",
        action="append",
        default=[],
        metavar="HOST",
        help="Host header accepted by a remotely hosted HTTP server. Repeatable. "
             "Can also set a comma-separated UNITY_MCP_HTTP_ALLOWED_HOSTS."
    )
    parser.add_argument(
        "--http-allowed-origin",
        action="append",
        default=[],
        metavar="ORIGIN",
        help="Browser origin accepted by a remotely hosted HTTP server. Repeatable. "
             "Can also set a comma-separated UNITY_MCP_HTTP_ALLOWED_ORIGINS."
    )
    parser.add_argument(
        "--api-key-validation-url",
        type=str,
        default=None,
        metavar="URL",
        help="External URL to validate API keys (POST with {'api_key': '...'}). "
             "Required when --http-remote-hosted is set. "
             "Can also set via UNITY_MCP_API_KEY_VALIDATION_URL."
    )
    parser.add_argument(
        "--api-key-login-url",
        type=str,
        default=None,
        metavar="URL",
        help="URL where users can obtain/manage API keys. "
             "Returned by /api/auth/login-url endpoint. "
             "Can also set via UNITY_MCP_API_KEY_LOGIN_URL."
    )
    parser.add_argument(
        "--api-key-cache-ttl",
        type=float,
        default=300.0,
        metavar="SECONDS",
        help="Cache TTL for validated API keys in seconds (default: 300). "
             "Can also set via UNITY_MCP_API_KEY_CACHE_TTL."
    )
    parser.add_argument(
        "--api-key-cache-max-entries",
        type=int,
        default=1024,
        metavar="COUNT",
        help="Maximum cached API-key validation results (default: 1024). "
             "Can also set via UNITY_MCP_API_KEY_CACHE_MAX_ENTRIES."
    )
    parser.add_argument(
        "--api-key-service-token-header",
        type=str,
        default=None,
        metavar="HEADER",
        help="Header name for service token sent to validation endpoint (e.g. X-Service-Token). "
             "Can also set via UNITY_MCP_API_KEY_SERVICE_TOKEN_HEADER."
    )
    parser.add_argument(
        "--api-key-service-token",
        type=str,
        default=None,
        metavar="TOKEN",
        help="Service token value sent to validation endpoint for server authentication. "
             "WARNING: Prefer UNITY_MCP_API_KEY_SERVICE_TOKEN env var in production to avoid process listing exposure."
    )
    parser.add_argument(
        "--unity-instance-token",
        type=str,
        default=None,
        metavar="TOKEN",
        help="Optional per-launch token set by Unity for deterministic lifecycle management. "
             "Used by Unity to validate it is stopping the correct process."
    )
    parser.add_argument(
        "--pidfile",
        type=str,
        default=None,
        metavar="PATH",
        help="Optional path where the server will write its PID on startup. "
             "Used by Unity to stop the exact process it launched when running in a terminal."
    )
    parser.add_argument(
        "--project-scoped-tools",
        action="store_true",
        help="Keep custom tools scoped to the active Unity project and enable the custom tools resource. "
             "Can also set via UNITY_MCP_PROJECT_SCOPED_TOOLS=true."
    )

    args = parser.parse_args()

    # Set environment variables from command line args
    if args.default_instance:
        os.environ["UNITY_MCP_DEFAULT_INSTANCE"] = args.default_instance
        logger.info(
            "Using default Unity instance from command-line: %s",
            args.default_instance,
        )

    # Set transport mode
    config.transport_mode = args.transport or os.environ.get(
        "UNITY_MCP_TRANSPORT", "stdio")
    logger.info("Transport mode: %s", config.transport_mode)

    config.http_remote_hosted = (
        bool(args.http_remote_hosted)
        or os.environ.get("UNITY_MCP_HTTP_REMOTE_HOSTED", "").lower() in ("true", "1", "yes", "on")
    )
    allowed_hosts_env = os.environ.get("UNITY_MCP_HTTP_ALLOWED_HOSTS", "")
    allowed_origins_env = os.environ.get("UNITY_MCP_HTTP_ALLOWED_ORIGINS", "")
    config.http_allowed_hosts = tuple(
        item.strip()
        for item in [*args.http_allowed_host, *allowed_hosts_env.split(",")]
        if item.strip()
    )
    config.http_allowed_origins = tuple(
        item.strip()
        for item in [*args.http_allowed_origin, *allowed_origins_env.split(",")]
        if item.strip()
    )
    try:
        config.http_session_idle_timeout_seconds = float(
            os.environ.get(
                "UNITY_MCP_HTTP_SESSION_IDLE_TIMEOUT",
                str(args.http_session_idle_timeout),
            )
        )
    except ValueError:
        logger.error("HTTP session idle timeout must be a number")
        raise SystemExit(2)
    try:
        config.http_max_sessions = int(
            os.environ.get(
                "UNITY_MCP_HTTP_MAX_SESSIONS",
                str(args.http_max_sessions),
            )
        )
    except ValueError:
        logger.error("HTTP max sessions must be an integer")
        raise SystemExit(2)
    if config.http_session_idle_timeout_seconds <= 0:
        logger.error("HTTP session idle timeout must be positive")
        raise SystemExit(2)
    if config.http_max_sessions <= 0:
        logger.error("HTTP max sessions must be positive")
        raise SystemExit(2)

    # API key authentication configuration
    config.api_key_validation_url = (
        args.api_key_validation_url
        or os.environ.get("UNITY_MCP_API_KEY_VALIDATION_URL")
    )
    config.api_key_login_url = (
        args.api_key_login_url
        or os.environ.get("UNITY_MCP_API_KEY_LOGIN_URL")
    )
    try:
        cache_ttl_env = os.environ.get("UNITY_MCP_API_KEY_CACHE_TTL")
        config.api_key_cache_ttl = (
            float(cache_ttl_env) if cache_ttl_env else args.api_key_cache_ttl
        )
    except ValueError:
        logger.warning(
            "Invalid UNITY_MCP_API_KEY_CACHE_TTL value, using default 300.0"
        )
        config.api_key_cache_ttl = 300.0
    try:
        cache_max_env = os.environ.get("UNITY_MCP_API_KEY_CACHE_MAX_ENTRIES")
        config.api_key_cache_max_entries = (
            int(cache_max_env)
            if cache_max_env
            else args.api_key_cache_max_entries
        )
    except ValueError:
        logger.error("API key cache max entries must be an integer")
        raise SystemExit(2)
    if config.api_key_cache_max_entries <= 0:
        logger.error("API key cache max entries must be positive")
        raise SystemExit(2)

    # Service token for authenticating to validation endpoint
    config.api_key_service_token_header = (
        args.api_key_service_token_header
        or os.environ.get("UNITY_MCP_API_KEY_SERVICE_TOKEN_HEADER")
    )
    config.api_key_service_token = (
        args.api_key_service_token
        or os.environ.get("UNITY_MCP_API_KEY_SERVICE_TOKEN")
    )

    # Validate: remote-hosted HTTP mode requires API key validation URL
    if config.http_remote_hosted and config.transport_mode == "http" and not config.api_key_validation_url:
        logger.error(
            "--http-remote-hosted requires --api-key-validation-url or "
            "UNITY_MCP_API_KEY_VALIDATION_URL environment variable"
        )
        raise SystemExit(1)
    if (
        config.http_remote_hosted
        and config.transport_mode == "http"
        and not config.http_allowed_hosts
    ):
        logger.error(
            "--http-remote-hosted requires at least one --http-allowed-host "
            "or UNITY_MCP_HTTP_ALLOWED_HOSTS value"
        )
        raise SystemExit(1)

    http_url = os.environ.get("UNITY_MCP_HTTP_URL", args.http_url)
    parsed_url = urlparse(http_url)

    # Allow individual host/port to override URL components
    http_host = args.http_host or os.environ.get(
        "UNITY_MCP_HTTP_HOST") or parsed_url.hostname or "127.0.0.1"

    # Safely parse optional environment port (may be None or non-numeric)
    _env_port_str = os.environ.get("UNITY_MCP_HTTP_PORT")
    try:
        _env_port = int(_env_port_str) if _env_port_str is not None else None
    except ValueError:
        logger.warning(
            "Invalid UNITY_MCP_HTTP_PORT value '%s', ignoring", _env_port_str)
        _env_port = None

    http_port = args.http_port or _env_port or parsed_url.port or 8080

    os.environ["UNITY_MCP_HTTP_HOST"] = http_host
    os.environ["UNITY_MCP_HTTP_PORT"] = str(http_port)

    # Optional lifecycle handshake for Unity-managed terminal launches
    if args.unity_instance_token:
        os.environ["UNITY_MCP_INSTANCE_TOKEN"] = args.unity_instance_token
    if args.pidfile:
        try:
            pid_dir = os.path.dirname(args.pidfile)
            if pid_dir:
                os.makedirs(pid_dir, exist_ok=True)
            with open(args.pidfile, "w", encoding="ascii") as f:
                f.write(str(os.getpid()))
        except Exception as exc:
            logger.warning(
                "Failed to write pidfile '%s': %s", args.pidfile, exc)

    if args.http_url != "http://127.0.0.1:8080":
        logger.info("HTTP URL set to: %s", http_url)
    if args.http_host:
        logger.info("HTTP host override: %s", http_host)
    if args.http_port:
        logger.info("HTTP port override: %s", http_port)

    # Explicit CLI/env overrides always win
    project_scoped_tools_explicit = (
        bool(args.project_scoped_tools)
        or os.environ.get("UNITY_MCP_PROJECT_SCOPED_TOOLS", "").lower() in ("true", "1", "yes", "on")
    )

    # If not explicitly set, check Unity status files for the default instance.
    # In stdio mode there is typically only one instance, so "first match wins" is fine.
    project_scoped_tools = project_scoped_tools_explicit
    if not project_scoped_tools_explicit:
        try:
            from transport.legacy.unity_connection import get_unity_connection_pool
            pool = get_unity_connection_pool()
            instances = pool.discover_all_instances()
            # If ANY discovered instance requests project-scoped tools, enable them
            for inst in instances:
                if getattr(inst, "project_scoped_tools", False):
                    project_scoped_tools = True
                    logger.info(
                        "Enabling project-scoped tools because Unity instance %s requested it",
                        inst.id,
                    )
                    break
        except Exception:
            logger.debug("Could not discover Unity instances for project-scoped tool default", exc_info=True)

    mcp = create_mcp_server(project_scoped_tools)

    # Determine transport mode
    if config.transport_mode == 'http':
        # Use the parsed host and port from URL/args
        http_url = os.environ.get("UNITY_MCP_HTTP_URL", args.http_url)
        parsed_url = urlparse(http_url)
        host = args.http_host or os.environ.get(
            "UNITY_MCP_HTTP_HOST") or parsed_url.hostname or "127.0.0.1"
        port = args.http_port or _env_port or parsed_url.port or 8080
        logger.info(
            "Starting FastMCP with HTTP transport on %s:%s "
            "(session idle timeout=%ss, max sessions=%s)",
            host,
            port,
            config.http_session_idle_timeout_seconds,
            config.http_max_sessions,
        )
        run_http_server(
            mcp,
            host=host,
            port=port,
            session_idle_timeout=config.http_session_idle_timeout_seconds,
            max_sessions=config.http_max_sessions,
            remote_hosted=config.http_remote_hosted,
            allowed_hosts=config.http_allowed_hosts,
            allowed_origins=config.http_allowed_origins,
        )
    else:
        # Use stdio transport for traditional MCP
        logger.info("Starting FastMCP with stdio transport")
        mcp.run(transport='stdio')


# Run the server
if __name__ == "__main__":
    main()
