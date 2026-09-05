"""Focused tests for the bounded server logging pipeline."""

from __future__ import annotations

import io
import logging
import queue
from contextlib import redirect_stdout
from logging.handlers import RotatingFileHandler

from core.config import ServerConfig
from core.logging_config import (
    DropAwareQueueHandler,
    WindowsSafeRotatingFileHandler,
    configure_server_logging,
)


_TOUCHED_LOGGERS = (
    "mcp-for-unity-server",
    "unity-mcp-telemetry",
    "httpx",
    "urllib3",
    "mcp.server.lowlevel.server",
)


def _logger_state(logger: logging.Logger):
    return list(logger.handlers), logger.level, logger.propagate


def _restore_logger(logger: logging.Logger, state) -> None:
    handlers, level, propagate = state
    logger.handlers.clear()
    logger.handlers.extend(handlers)
    logger.setLevel(level)
    logger.propagate = propagate


def _configure(tmp_path, stderr: io.StringIO, **kwargs):
    root = logging.getLogger()
    states = {"": _logger_state(root)}
    states.update(
        (name, _logger_state(logging.getLogger(name)))
        for name in _TOUCHED_LOGGERS
    )
    runtime = configure_server_logging(
        ServerConfig(log_level="DEBUG"),
        log_dir=tmp_path,
        stream=stderr,
        **kwargs,
    )

    def restore() -> None:
        runtime.stop(timeout_seconds=2.0)
        _restore_logger(root, states[""])
        for name in _TOUCHED_LOGGERS:
            _restore_logger(logging.getLogger(name), states[name])

    return runtime, restore


def test_application_and_telemetry_records_reach_each_sink_once(tmp_path):
    stderr = io.StringIO()
    runtime, restore = _configure(tmp_path, stderr)
    try:
        logging.getLogger("mcp-for-unity-server").info("application-marker")
        logging.getLogger("unity-mcp-telemetry").info("telemetry-marker")
        assert runtime.stop(timeout_seconds=2.0)

        stderr_text = stderr.getvalue()
        file_text = (tmp_path / "unity_mcp_server.log").read_text(encoding="utf-8")
        for marker in ("application-marker", "telemetry-marker"):
            assert stderr_text.count(marker) == 1
            assert file_text.count(marker) == 1
    finally:
        restore()


def test_logging_never_writes_protocol_stdout(tmp_path):
    stderr = io.StringIO()
    stdout = io.StringIO()
    runtime, restore = _configure(tmp_path, stderr)
    try:
        with redirect_stdout(stdout):
            logging.getLogger("mcp-for-unity-server").warning("stderr-only")
            assert runtime.stop(timeout_seconds=2.0)

        assert stdout.getvalue() == ""
        assert stderr.getvalue().count("stderr-only") == 1
    finally:
        restore()


def test_queue_saturation_is_bounded_counted_and_preserves_warning():
    log_queue: queue.Queue = queue.Queue(maxsize=1)
    fallback_stream = io.StringIO()
    fallback = logging.StreamHandler(fallback_stream)
    fallback.setLevel(logging.DEBUG)
    handler = DropAwareQueueHandler(log_queue, (fallback,))

    logger = logging.getLogger("queue-saturation-test")
    logger.setLevel(logging.DEBUG)
    logger.handlers = [handler]
    logger.propagate = False
    try:
        logger.debug("fills-queue")
        logger.info("dropped-info")
        logger.warning("preserved-warning")

        assert log_queue.qsize() == 1
        assert handler.dropped_by_level == {"INFO": 1, "WARNING": 1}
        assert handler.dropped_total == 2
        assert fallback_stream.getvalue().count("preserved-warning") == 1
        assert "dropped-info" not in fallback_stream.getvalue()
    finally:
        logger.handlers.clear()
        logger.propagate = True


def test_queue_handler_defers_message_formatting_to_listener():
    class ExpensiveMessage:
        calls = 0

        def __str__(self) -> str:
            self.calls += 1
            return "formatted-later"

    log_queue: queue.Queue = queue.Queue(maxsize=1)
    handler = DropAwareQueueHandler(log_queue, ())
    message = ExpensiveMessage()
    record = logging.LogRecord(
        "lazy-format-test",
        logging.INFO,
        __file__,
        1,
        message,
        (),
        None,
    )

    handler.handle(record)
    assert message.calls == 0

    queued_record = log_queue.get_nowait()
    logging.Formatter("%(message)s").format(queued_record)
    assert message.calls == 1


def test_shutdown_drains_pending_records(tmp_path):
    stderr = io.StringIO()
    runtime, restore = _configure(
        tmp_path,
        stderr,
        queue_capacity=512,
        shutdown_timeout_seconds=2.0,
    )
    try:
        logger = logging.getLogger("mcp-for-unity-server")
        for index in range(100):
            logger.info("drain-marker-%03d", index)

        assert runtime.stop(timeout_seconds=2.0)
        file_text = (tmp_path / "unity_mcp_server.log").read_text(encoding="utf-8")
        assert file_text.count("drain-marker-") == 100
        assert runtime.dropped_total == 0
    finally:
        restore()


def test_shutdown_tolerates_an_already_closed_stderr_stream(tmp_path):
    stderr = io.StringIO()
    runtime, restore = _configure(tmp_path, stderr)
    try:
        logging.getLogger("mcp-for-unity-server").info("before-close")
        stderr.close()
        assert runtime.stop(timeout_seconds=2.0)
    finally:
        restore()


def test_noisy_third_party_debug_is_filtered_but_warning_is_routed(tmp_path):
    stderr = io.StringIO()
    runtime, restore = _configure(tmp_path, stderr)
    try:
        noisy = logging.getLogger("httpx")
        noisy.debug("filtered-noise")
        noisy.warning("retained-warning")
        assert runtime.stop(timeout_seconds=2.0)

        text = stderr.getvalue()
        assert "filtered-noise" not in text
        assert text.count("retained-warning") == 1
    finally:
        restore()


def test_windows_locked_rotation_is_best_effort(monkeypatch, tmp_path):
    handler = WindowsSafeRotatingFileHandler(
        tmp_path / "locked.log",
        maxBytes=1,
        backupCount=1,
        encoding="utf-8",
    )

    def locked_rollover(_handler):
        raise PermissionError("file is open")

    monkeypatch.setattr(RotatingFileHandler, "doRollover", locked_rollover)
    try:
        handler.doRollover()
    finally:
        handler.close()
