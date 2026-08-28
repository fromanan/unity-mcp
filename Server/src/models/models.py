from typing import Any
from datetime import datetime
from pydantic import BaseModel, Field


class MCPResponse(BaseModel):
    success: bool
    message: str | None = None
    error: str | None = None
    data: Any | None = None
    # Optional hint for clients about how to handle the response.
    # Supported values:
    #   - "retry": Unity is temporarily reloading; call should be retried politely.
    hint: str | None = None


class ToolParameterModel(BaseModel):
    name: str = Field(min_length=1, max_length=128)
    description: str | None = Field(default=None, max_length=4096)
    type: str = Field(default="string", min_length=1, max_length=128)
    required: bool = Field(default=True)
    default_value: str | None = Field(default=None, max_length=4096)


class ToolDefinitionModel(BaseModel):
    name: str = Field(min_length=1, max_length=128)
    description: str | None = Field(default=None, max_length=16384)
    structured_output: bool | None = True
    requires_polling: bool | None = False
    poll_action: str | None = Field(default="status", max_length=128)
    max_poll_seconds: int = Field(default=0, ge=0, le=3600)
    group: str | None = Field(default=None, max_length=128)
    is_built_in: bool = False
    parameters: list[ToolParameterModel] = Field(
        default_factory=list,
        max_length=128,
    )


class UnityInstanceInfo(BaseModel):
    """Information about a Unity Editor instance"""
    id: str  # "ProjectName@hash" or fallback to hash
    name: str  # Project name extracted from path
    path: str  # Full project path (Assets folder)
    hash: str  # 8-char hash of project path
    port: int  # TCP port
    status: str  # "running", "reloading", "offline"
    last_heartbeat: datetime | None = None
    unity_version: str | None = None
    project_scoped_tools: bool = False

    def to_dict(self) -> dict[str, Any]:
        """Convert to dictionary for JSON serialization"""
        return {
            "id": self.id,
            "name": self.name,
            "path": self.path,
            "hash": self.hash,
            "port": self.port,
            "status": self.status,
            "last_heartbeat": self.last_heartbeat.isoformat() if self.last_heartbeat else None,
            "unity_version": self.unity_version,
            "project_scoped_tools": self.project_scoped_tools,
        }
