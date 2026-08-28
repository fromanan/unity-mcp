from __future__ import annotations

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.registry import DEFAULT_ENABLED_GROUPS, get_registered_tools
from services.tools.rendering_workflow import (
    inspect_material,
    inspect_render_target,
    inspect_shader_graph,
    inspect_texture,
    manage_rendering_authoring,
    profile_render_target,
    render_probe,
    sample_material,
    validate_render_contract,
)


@pytest.fixture
def mock_unity(monkeypatch):
    calls: list[dict[str, object]] = []

    async def fake_send(send_fn, unity_instance, tool_name, params):
        calls.append({
            "unity_instance": unity_instance,
            "tool_name": tool_name,
            "params": params,
        })
        return {
            "success": True,
            "message": "ok",
            "data": {"effective_asset_path": params.get("asset_path")},
        }

    monkeypatch.setattr(
        "services.tools.rendering_workflow.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-rendering"),
    )
    monkeypatch.setattr(
        "services.tools.rendering_workflow.send_with_unity_instance",
        fake_send,
    )
    monkeypatch.setattr(
        "services.tools.rendering_workflow.wait_for_editor_ready",
        AsyncMock(return_value=(True, 0.25)),
    )
    return calls


def test_bootstrap_profile_keeps_rendering_groups_opt_in():
    assert "rendering_inspect" not in DEFAULT_ENABLED_GROUPS
    assert "rendering_authoring" not in DEFAULT_ENABLED_GROUPS


def test_tool_metadata_splits_inspection_from_authoring():
    tools = {entry["name"]: entry for entry in get_registered_tools()}
    inspector_names = {
        "inspect_render_target",
        "inspect_material",
        "inspect_texture",
        "inspect_shader_graph",
        "validate_render_contract",
        "sample_material",
        "render_probe",
        "profile_render_target",
    }
    for name in inspector_names:
        assert tools[name]["group"] == "rendering_inspect"
        assert tools[name]["unity_target"] == "inspect_rendering"
    assert tools["manage_rendering_authoring"]["group"] == "rendering_authoring"
    assert tools["manage_rendering_authoring"]["unity_target"] == "manage_rendering_authoring"


@pytest.mark.parametrize(
    ("call", "expected_action"),
    [
        (lambda: inspect_render_target(SimpleNamespace(), target="Root"), "inspect_render_target"),
        (lambda: inspect_material(SimpleNamespace(), material_path="Assets/Test.mat"), "inspect_material"),
        (lambda: inspect_texture(SimpleNamespace(), texture_path="Assets/Test.png"), "inspect_texture"),
        (lambda: inspect_shader_graph(SimpleNamespace(), shader_path="Assets/Test.shadergraph"), "inspect_shader_graph"),
        (lambda: profile_render_target(SimpleNamespace(), target="Root"), "profile_render_target"),
    ],
)
def test_inspection_tools_route_to_shared_read_handler(mock_unity, call, expected_action):
    result = asyncio.run(call())
    assert result["success"] is True
    assert mock_unity[-1]["tool_name"] == "inspect_rendering"
    assert mock_unity[-1]["params"]["action"] == expected_action
    assert mock_unity[-1]["unity_instance"] == "unity-instance-rendering"


def test_validate_contract_parses_json_and_forwards_strict(mock_unity):
    result = asyncio.run(
        validate_render_contract(
            SimpleNamespace(),
            material_path="Assets/Test.mat",
            contracts='{"_MaskMap":{"semantic_contract":"urp_mask"}}',
            strict=True,
        )
    )
    assert result["success"] is True
    params = mock_unity[-1]["params"]
    assert params["contracts"]["_MaskMap"]["semantic_contract"] == "urp_mask"
    assert params["strict"] is True


def test_validate_contract_rejects_invalid_json_without_transport(mock_unity):
    result = asyncio.run(
        validate_render_contract(
            SimpleNamespace(),
            material_path="Assets/Test.mat",
            contracts="{invalid",
        )
    )
    assert result["success"] is False
    assert "Invalid JSON" in result["message"]
    assert mock_unity == []


def test_render_probe_forwards_locked_capture_manifest(mock_unity):
    result = asyncio.run(
        render_probe(
            SimpleNamespace(),
            camera="Main Camera",
            target="Wall",
            scope="target",
            width=1920,
            height=1080,
            channel="wireframe",
            warmup_frames=2,
            quality_level=3,
        )
    )
    assert result["success"] is True
    params = mock_unity[-1]["params"]
    assert params["scope"] == "target"
    assert params["width"] == 1920
    assert params["height"] == 1080
    assert params["channel"] == "wireframe"
    assert params["warmup_frames"] == 2
    assert params["quality_level"] == 3


def test_sample_material_parses_overrides_and_forwards_locked_ab_manifest(mock_unity):
    result = asyncio.run(
        sample_material(
            SimpleNamespace(),
            material_path="Assets/Primary.mat",
            profile="foliage",
            compare_to_material_path="Assets/Reference.mat",
            property_overrides='{"_BaseColor":[0.2,0.4,0.6,1.0]}',
            max_resolution=512,
            warmup_frames=2,
            include_image=False,
            output_path="Library/MCPForUnity/MaterialSamples/Tests/sample.png",
            cache_mode="refresh",
        )
    )
    assert result["success"] is True
    params = mock_unity[-1]["params"]
    assert params["action"] == "sample_material"
    assert params["material_path"] == "Assets/Primary.mat"
    assert params["compare_to_material_path"] == "Assets/Reference.mat"
    assert params["profile"] == "foliage"
    assert params["property_overrides"]["_BaseColor"] == [0.2, 0.4, 0.6, 1.0]
    assert params["max_resolution"] == 512
    assert params["warmup_frames"] == 2
    assert params["include_image"] is False
    assert params["cache_mode"] == "refresh"


@pytest.mark.parametrize("overrides", ["[1,2]", "{invalid"])
def test_sample_material_rejects_non_object_or_invalid_overrides_without_transport(
    mock_unity,
    overrides,
):
    result = asyncio.run(
        sample_material(
            SimpleNamespace(),
            material_path="Assets/Test.mat",
            property_overrides=overrides,
        )
    )
    assert result["success"] is False
    assert "property_overrides" in result["message"] or "Invalid JSON" in result["message"]
    assert mock_unity == []


def test_authoring_apply_requires_sha_before_transport(mock_unity):
    result = asyncio.run(
        manage_rendering_authoring(
            SimpleNamespace(),
            asset_path="Assets/Test.mat",
            asset_kind="material",
            operations=[],
            dry_run=False,
        )
    )
    assert result["success"] is False
    assert "expected_sha256" in result["message"]
    assert mock_unity == []


def test_authoring_dry_run_is_default_and_does_not_wait(mock_unity, monkeypatch):
    wait_mock = AsyncMock(return_value=(True, 0.0))
    monkeypatch.setattr(
        "services.tools.rendering_workflow.wait_for_editor_ready",
        wait_mock,
    )
    result = asyncio.run(
        manage_rendering_authoring(
            SimpleNamespace(),
            asset_path="Assets/Test.mat",
            asset_kind="material",
            operations='[{"op":"set_float","property":"_Metallic","value":0}]',
        )
    )
    assert result["success"] is True
    assert mock_unity[-1]["params"]["dry_run"] is True
    assert isinstance(mock_unity[-1]["params"]["operations"], list)
    wait_mock.assert_not_awaited()


def test_authoring_apply_waits_and_runs_semantic_post_validation(mock_unity):
    result = asyncio.run(
        manage_rendering_authoring(
            SimpleNamespace(),
            asset_path="Assets/Test.mat",
            asset_kind="material",
            operations=[{"op": "set_float", "property": "_Metallic", "value": 0}],
            dry_run=False,
            expected_sha256="a" * 64,
        )
    )
    assert result["success"] is True
    assert len(mock_unity) == 2
    assert mock_unity[0]["tool_name"] == "manage_rendering_authoring"
    assert mock_unity[1]["tool_name"] == "inspect_rendering"
    assert mock_unity[1]["params"]["action"] == "inspect_material"
    assert result["editor_readiness"]["ready_for_tools"] is True
    assert result["post_validation"]["success"] is True
