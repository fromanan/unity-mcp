from types import SimpleNamespace

import http_runtime


def test_run_http_server_configures_one_protocol_keepalive(monkeypatch):
    captured: dict[str, object] = {}
    app = SimpleNamespace(state=SimpleNamespace(session_manager=None))

    monkeypatch.setattr(
        http_runtime,
        "create_bounded_streamable_http_app",
        lambda *args, **kwargs: app,
    )

    class FakeConfig:
        def __init__(self, **kwargs):
            captured.update(kwargs)

    class FakeServer:
        def __init__(self, config):
            self.config = config
            self.should_exit = False

        async def serve(self):
            return None

    monkeypatch.setattr(http_runtime.uvicorn, "Config", FakeConfig)
    monkeypatch.setattr(http_runtime.uvicorn, "Server", FakeServer)

    http_runtime.run_http_server(
        object(),
        host="127.0.0.1",
        port=8080,
        session_idle_timeout=1800,
        max_sessions=16,
    )

    assert captured["ws_ping_interval"] == 20.0
    assert captured["ws_ping_timeout"] == 60.0
