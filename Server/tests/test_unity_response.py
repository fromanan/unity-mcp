from models.models import MCPResponse
from models.unity_response import normalize_unity_response, parse_resource_response


def test_normalize_unity_response_preserves_structured_error_metadata():
    response = {
        "status": "error",
        "result": {
            "code": "prefab_instance_destroyed",
            "message": "The prefab instance was destroyed.",
            "error": "prefab_instance_destroyed",
            "hint": "Inspect lifecycle callbacks.",
            "warnings": [{"type": "Error", "message": "Fixture destroyed itself."}],
            "data": {"stateChanged": True, "retryable": False},
        },
    }

    normalized = normalize_unity_response(response)

    assert normalized["success"] is False
    assert normalized["code"] == "prefab_instance_destroyed"
    assert normalized["message"] == "The prefab instance was destroyed."
    assert normalized["hint"] == "Inspect lifecycle callbacks."
    assert normalized["warnings"][0]["type"] == "Error"
    assert normalized["data"] == {"stateChanged": True, "retryable": False}


def test_parse_resource_response_preserves_structured_error_metadata():
    parsed = parse_resource_response(
        {
            "success": False,
            "code": "play_mode_create_blocked",
            "message": "Creation is blocked in Play Mode.",
            "error": "play_mode_create_blocked",
            "hint": "Exit Play Mode.",
            "data": {"stateChanged": False},
        },
        MCPResponse,
    )

    assert parsed.code == "play_mode_create_blocked"
    assert parsed.error == "play_mode_create_blocked"
    assert parsed.hint == "Exit Play Mode."
    assert parsed.data == {"stateChanged": False}
