from __future__ import annotations

from fastmcp import FastMCP
import pytest
from starlette.testclient import TestClient
from unittest.mock import AsyncMock, patch

from core.config import config
from models.models import ToolDefinitionModel, ToolParameterModel
from services.custom_tool_service import CustomToolService
from transport.bounded_streamable_http import create_bounded_streamable_http_app


def _service_app():
    mcp = FastMCP("custom-tool-hardening")
    service = CustomToolService(mcp)
    app = create_bounded_streamable_http_app(
        mcp,
        streamable_http_path="/mcp",
        session_idle_timeout=30,
        max_sessions=4,
        host_origin_protection=False,
    )
    return service, app


def test_legacy_registration_route_is_hidden_in_remote_mode():
    previous = config.http_remote_hosted
    config.http_remote_hosted = True
    try:
        _, app = _service_app()
        with TestClient(app) as client:
            response = client.post(
                "/register-tools",
                json={"project_id": "p", "tools": []},
            )
        assert response.status_code == 404
    finally:
        config.http_remote_hosted = previous


def test_legacy_registration_route_rejects_oversized_body():
    previous = config.http_remote_hosted
    config.http_remote_hosted = False
    try:
        _, app = _service_app()
        body = b"x" * (CustomToolService.MAX_REGISTRATION_BODY_BYTES + 1)
        with TestClient(app) as client:
            response = client.post(
                "/register-tools",
                content=body,
                headers={"Content-Type": "application/json"},
            )
        assert response.status_code == 413
    finally:
        config.http_remote_hosted = previous


def test_global_tools_are_removed_when_last_owner_disconnects():
    service, _ = _service_app()
    tool = ToolDefinitionModel(name="session_owned_custom_tool")
    service.register_global_tools([tool], owner_id="session-a")
    assert "session_owned_custom_tool" in service._global_tools

    service.unregister_global_tools_for_owner("session-a")
    assert "session_owned_custom_tool" not in service._global_tools
    assert "session_owned_custom_tool" not in service._global_tool_owners


def test_owner_registration_replaces_its_previous_snapshot():
    service, _ = _service_app()
    service.replace_global_tools_for_owner(
        [
            ToolDefinitionModel(name="kept_tool"),
            ToolDefinitionModel(name="removed_tool"),
        ],
        owner_id="session-a",
    )

    service.replace_global_tools_for_owner(
        [ToolDefinitionModel(name="kept_tool")],
        owner_id="session-a",
    )

    assert "kept_tool" in service._global_tools
    assert "removed_tool" not in service._global_tools
    assert "removed_tool" not in service._global_tool_owners


def test_legacy_project_registry_is_bounded():
    service, _ = _service_app()
    for index in range(CustomToolService.MAX_LEGACY_PROJECTS + 3):
        service._register_project_tools(
            f"project-{index}",
            [ToolDefinitionModel(name=f"tool-{index}")],
            project_hash=f"hash-{index}",
        )

    assert len(service._project_tools) == CustomToolService.MAX_LEGACY_PROJECTS
    assert "project-0" not in service._project_tools
    assert "hash-0" not in service._hash_to_project


def test_same_name_owner_schemas_merge_to_bounded_dispatch_signature():
    service, _ = _service_app()
    service.register_global_tools(
        [
            ToolDefinitionModel(
                name="project_specific_tool",
                parameters=[
                    ToolParameterModel(name="count", type="integer"),
                    ToolParameterModel(name="shared", type="string"),
                ],
            )
        ],
        owner_id="session-a",
    )
    service.register_global_tools(
        [
            ToolDefinitionModel(
                name="project_specific_tool",
                parameters=[
                    ToolParameterModel(name="enabled", type="boolean"),
                    ToolParameterModel(name="shared", type="integer"),
                ],
            )
        ],
        owner_id="session-b",
    )

    merged = service._global_tools["project_specific_tool"]
    by_name = {parameter.name: parameter for parameter in merged.parameters}
    assert list(by_name) == ["count", "enabled", "shared"]
    assert all(parameter.required is False for parameter in by_name.values())
    assert by_name["shared"].type == "any"
    assert "active Unity instance" in (merged.description or "")


@pytest.mark.asyncio
async def test_active_project_definition_wins_over_merged_global_schema():
    service, _ = _service_app()
    global_definition = ToolDefinitionModel(
        name="project_specific_tool",
        parameters=[ToolParameterModel(name="generic", required=False)],
    )
    project_definition = ToolDefinitionModel(
        name="project_specific_tool",
        parameters=[ToolParameterModel(name="exact", type="integer")],
    )
    service.register_global_tools([global_definition], owner_id="session-a")

    with patch.object(
        service,
        "_project_tools",
        {"project-a": {"project_specific_tool": project_definition}},
    ), patch(
        "services.custom_tool_service.PluginHub.get_tool_definition",
        new=AsyncMock(),
    ) as hub_lookup:
        resolved = await service.get_tool_definition(
            "project-a",
            "project_specific_tool",
        )

    assert resolved == project_definition
    hub_lookup.assert_not_awaited()
