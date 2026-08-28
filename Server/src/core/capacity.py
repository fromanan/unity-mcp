"""Centralized bounded capacity settings for MCP transport state."""

from __future__ import annotations

import logging
import os

logger = logging.getLogger("mcp-for-unity-server")


def bounded_int_env(
    name: str,
    default: int,
    minimum: int,
    maximum: int,
) -> int:
    raw = os.environ.get(name)
    try:
        value = int(raw) if raw is not None else default
    except ValueError:
        logger.warning("Invalid %s=%r, using default %d", name, raw, default)
        value = default
    return max(minimum, min(value, maximum))


COMMAND_MAX_BYTES = bounded_int_env(
    "UNITY_MCP_MAX_COMMAND_BYTES",
    4 * 1024 * 1024,
    16 * 1024,
    32 * 1024 * 1024,
)
SESSION_QUEUE_MAX_BYTES = bounded_int_env(
    "UNITY_MCP_MAX_SESSION_QUEUE_BYTES",
    8 * 1024 * 1024,
    64 * 1024,
    64 * 1024 * 1024,
)
TOOL_REGISTRATION_MAX_BYTES = bounded_int_env(
    "UNITY_MCP_MAX_TOOL_REGISTRATION_BYTES",
    1024 * 1024,
    64 * 1024,
    16 * 1024 * 1024,
)
EDITOR_STATE_MAX_BYTES = bounded_int_env(
    "UNITY_MCP_MAX_EDITOR_STATE_BYTES",
    2 * 1024 * 1024,
    64 * 1024,
    16 * 1024 * 1024,
)
PLUGIN_MAX_SESSIONS = bounded_int_env(
    "UNITY_MCP_MAX_PLUGIN_SESSIONS", 16, 1, 256
)
PLUGIN_MAX_SESSIONS_PER_USER = bounded_int_env(
    "UNITY_MCP_MAX_PLUGIN_SESSIONS_PER_USER", 8, 1, 64
)
PENDING_MAX_COMMANDS = bounded_int_env(
    "UNITY_MCP_MAX_PENDING_COMMANDS", 64, 1, 1024
)
AUTH_MAX_INFLIGHT = bounded_int_env(
    "UNITY_MCP_MAX_AUTH_INFLIGHT", 128, 1, 4096
)
AUTH_MAX_CONCURRENCY = bounded_int_env(
    "UNITY_MCP_MAX_AUTH_CONCURRENCY", 16, 1, 256
)
