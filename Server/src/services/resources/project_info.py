import threading

from pydantic import BaseModel, Field
from fastmcp import Context

from models import MCPResponse
from models.unity_response import parse_resource_response
from services.registry import mcp_for_unity_resource
from services.tools import get_unity_instance_from_context
from transport.unity_transport import send_with_unity_instance
from transport.legacy.unity_connection import async_send_command_with_retry


class ProjectInfoData(BaseModel):
    """Project info data fields."""
    projectRoot: str = ""
    projectName: str = ""
    unityVersion: str = ""
    platform: str = ""
    assetsPath: str = ""


class ProjectInfoResponse(MCPResponse):
    """Static project configuration information."""
    data: ProjectInfoData = Field(default_factory=ProjectInfoData)


_cache_lock = threading.RLock()
_project_info_cache: dict[str, ProjectInfoData] = {}


def _copy_project_info(data: ProjectInfoData) -> ProjectInfoData:
    if hasattr(data, "model_copy"):
        return data.model_copy(deep=True)
    return ProjectInfoData.parse_obj(data.dict())  # type: ignore[attr-defined]


def clear_project_info_cache(instance_id: str | None = None) -> None:
    with _cache_lock:
        if instance_id is None:
            _project_info_cache.clear()
        else:
            _project_info_cache.pop(instance_id, None)


def get_cached_project_root(instance_id: str | None) -> str | None:
    if not instance_id:
        return None
    with _cache_lock:
        data = _project_info_cache.get(instance_id)
        return data.projectRoot if data is not None else None


@mcp_for_unity_resource(
    uri="mcpforunity://project/info",
    name="project_info",
    description="Static project information including root path, Unity version, and platform. This data rarely changes.\n\nURI: mcpforunity://project/info"
)
async def get_project_info(ctx: Context) -> ProjectInfoResponse | MCPResponse:
    """Get static project configuration information."""
    unity_instance = await get_unity_instance_from_context(ctx)
    if unity_instance:
        with _cache_lock:
            cached = _project_info_cache.get(unity_instance)
            if cached is not None:
                return ProjectInfoResponse(
                    success=True,
                    message="Retrieved cached project information.",
                    data=_copy_project_info(cached),
                )

    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "get_project_info",
        {}
    )
    parsed = parse_resource_response(response, ProjectInfoResponse)
    if unity_instance and isinstance(parsed, ProjectInfoResponse) and parsed.success:
        with _cache_lock:
            _project_info_cache[unity_instance] = _copy_project_info(parsed.data)
    return parsed
