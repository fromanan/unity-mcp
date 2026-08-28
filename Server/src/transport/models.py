from typing import Any
from pydantic import BaseModel, Field
from models.models import ToolDefinitionModel

# Outgoing (Server -> Plugin)


class WelcomeMessage(BaseModel):
    type: str = "welcome"
    serverTimeout: int
    keepAliveInterval: int
    capabilities: list[str] = Field(default_factory=list)


class RegisteredMessage(BaseModel):
    type: str = "registered"
    session_id: str


class ExecuteCommandMessage(BaseModel):
    type: str = "execute"
    id: str
    name: str
    params: dict[str, Any]
    timeout: float


class PingMessage(BaseModel):
    """Server-initiated ping to detect dead connections."""
    type: str = "ping"

# Incoming (Plugin -> Server)


class RegisterMessage(BaseModel):
    type: str = "register"
    project_name: str = Field(default="Unknown Project", max_length=256)
    project_hash: str = Field(max_length=128)
    unity_version: str = Field(default="Unknown", max_length=128)
    project_path: str | None = Field(default=None, max_length=4096)  # Full path to project root (for focus nudging)


class RegisterToolsMessage(BaseModel):
    type: str = "register_tools"
    tools: list[ToolDefinitionModel] = Field(max_length=256)


class PongMessage(BaseModel):
    type: str = "pong"
    session_id: str | None = None


class EditorStateMessage(BaseModel):
    type: str = "editor_state"
    session_id: str | None = None
    state: dict[str, Any] = Field(default_factory=dict)


class EditorHeartbeatMessage(BaseModel):
    type: str = "editor_heartbeat"
    session_id: str | None = None
    editor_heartbeat_unix_ms: int | None = None


class CommandResultMessage(BaseModel):
    type: str = "command_result"
    id: str
    result: dict[str, Any] = Field(default_factory=dict)

# Session Info (API response)


class SessionDetails(BaseModel):
    project: str
    hash: str
    unity_version: str
    connected_at: str


class SessionList(BaseModel):
    sessions: dict[str, SessionDetails]
