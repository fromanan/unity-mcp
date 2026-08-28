import os
import importlib


def _enable_telemetry(monkeypatch):
    for name in (
        "DISABLE_TELEMETRY",
        "UNITY_MCP_DISABLE_TELEMETRY",
        "MCP_DISABLE_TELEMETRY",
    ):
        monkeypatch.delenv(name, raising=False)


def test_endpoint_rejects_non_http(tmp_path, monkeypatch):
    # Point data dir to temp to avoid touching real files
    monkeypatch.setenv("APPDATA", str(tmp_path))
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))
    monkeypatch.setenv("UNITY_MCP_TELEMETRY_ENDPOINT", "file:///etc/passwd")

    # Import the telemetry module
    telemetry = importlib.import_module("core.telemetry")
    importlib.reload(telemetry)

    tc = telemetry.TelemetryCollector()
    try:
        # Should have fallen back to default endpoint
        assert tc.config.endpoint == tc.config.default_endpoint
    finally:
        tc.shutdown()


def test_config_preferred_then_env_override(tmp_path, monkeypatch):
    # Simulate config telemetry endpoint
    monkeypatch.setenv("APPDATA", str(tmp_path))
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))
    monkeypatch.delenv("UNITY_MCP_TELEMETRY_ENDPOINT", raising=False)

    # Patch config.telemetry_endpoint via import mocking
    cfg_mod = importlib.import_module("src.core.config")
    old_endpoint = cfg_mod.config.telemetry_endpoint
    cfg_mod.config.telemetry_endpoint = "https://example.com/telemetry"
    try:
        telemetry = importlib.import_module("core.telemetry")
        importlib.reload(telemetry)
        tc = telemetry.TelemetryCollector()
        try:
            # When no env override is set, config endpoint is preferred
            assert tc.config.endpoint == "https://example.com/telemetry"
        finally:
            tc.shutdown()

        # Env should override config
        monkeypatch.setenv("UNITY_MCP_TELEMETRY_ENDPOINT",
                           "https://override.example/ep")
        importlib.reload(telemetry)
        tc2 = telemetry.TelemetryCollector()
        try:
            assert tc2.config.endpoint == "https://override.example/ep"
        finally:
            tc2.shutdown()
    finally:
        cfg_mod.config.telemetry_endpoint = old_endpoint


def test_uuid_preserved_on_malformed_milestones(tmp_path, monkeypatch):
    _enable_telemetry(monkeypatch)
    monkeypatch.setenv("APPDATA", str(tmp_path))
    monkeypatch.setenv("XDG_DATA_HOME", str(tmp_path))

    # Import the telemetry module
    telemetry = importlib.import_module("core.telemetry")
    importlib.reload(telemetry)

    tc1 = telemetry.TelemetryCollector()
    try:
        first_uuid = tc1._customer_uuid

        # Write malformed milestones
        tc1.config.milestones_file.write_text("{not-json}", encoding="utf-8")
    finally:
        tc1.shutdown()

    # Reload collector; UUID should remain same despite bad milestones
    importlib.reload(telemetry)
    tc2 = telemetry.TelemetryCollector()
    try:
        assert tc2._customer_uuid == first_uuid
    finally:
        tc2.shutdown()
