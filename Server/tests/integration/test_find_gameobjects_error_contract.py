import pytest

import services.tools.find_gameobjects as find_go_mod

from .test_helpers import DummyContext


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "unity_response",
    [
        {
            "success": False,
            "code": "ambiguous_component_type",
            "error": "ambiguous_component_type",
            "message": "Ambiguous type reference 'PlayerController'. Found 2 matches.",
            "hint": "Use one of the fully-qualified candidate names.",
            "data": {
                "searchMethod": "by_component",
                "searchTerm": "PlayerController",
                "candidateCount": 2,
                "candidates": ["Game.PlayerController", "Demo.PlayerController"],
            },
        },
        {
            "success": False,
            "code": "component_type_not_found",
            "error": "component_type_not_found",
            "message": "Type 'MissingController' not found in loaded runtime assemblies.",
            "hint": "Use a fully-qualified name and ensure the defining script compiled successfully.",
            "data": {
                "searchMethod": "by_component",
                "searchTerm": "MissingController",
                "candidateCount": 0,
                "candidates": [],
            },
        },
    ],
)
async def test_find_gameobjects_preserves_component_resolution_errors(
    monkeypatch,
    unity_response,
):
    async def fake_preflight(_ctx, **_kwargs):
        return None

    async def fake_send(_cmd, _params, **_kwargs):
        return unity_response

    monkeypatch.setattr(find_go_mod, "preflight", fake_preflight)
    monkeypatch.setattr(find_go_mod, "async_send_command_with_retry", fake_send)

    result = await find_go_mod.find_gameobjects(
        DummyContext(),
        unity_response["data"]["searchTerm"],
        search_method="by_component",
    )

    assert result == unity_response
