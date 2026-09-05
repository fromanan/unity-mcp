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
    connection_id: str | None = None
    ready_required: bool = False


class PluginReadyAckMessage(BaseModel):
    type: str = "plugin_ready_ack"
    session_id: str
    connection_id: str


class ExecuteCommandMessage(BaseModel):
    type: str = "execute"
    id: str
    name: str
    params: dict[str, Any]
    timeout: float


# Incoming (Plugin -> Server)


class RegisterMessage(BaseModel):
    type: str = "register"
    project_name: str = Field(default="Unknown Project", max_length=256)
    project_hash: str = Field(max_length=128)
    unity_version: str = Field(default="Unknown", max_length=128)
    project_path: str | None = Field(default=None, max_length=4096)  # Full path to project root (for focus nudging)
    connection_id: str | None = Field(default=None, max_length=128)
    capabilities: list[str] = Field(default_factory=list, max_length=32)


class RegisterToolsMessage(BaseModel):
    type: str = "register_tools"
    tools: list[ToolDefinitionModel] = Field(max_length=256)


class PluginReadyMessage(BaseModel):
    type: str = "plugin_ready"
    session_id: str
    connection_id: str


class ClientLifecycleMessage(BaseModel):
    type: str = "client_lifecycle"
    state: str
    session_id: str | None = None
    connection_id: str | None = None


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
