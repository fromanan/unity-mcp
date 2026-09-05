"""Bounded, asynchronous logging configuration for the MCP server."""

from __future__ import annotations

import atexit
import copy
import logging
import os
import queue
import sys
import threading
import time
from collections import defaultdict
from logging.handlers import QueueHandler, QueueListener, RotatingFileHandler
from typing import IO, Iterable

from utils.log_paths import resolve_log_dir


_APPLICATION_LOGGERS = ("mcp-for-unity-server", "unity-mcp-telemetry")
_NOISY_LOGGERS = ("httpx", "urllib3", "mcp.server.lowlevel.server")


class WindowsSafeRotatingFileHandler(RotatingFileHandler):
    """Skip a rollover attempt when another Windows process locks the log."""

    def doRollover(self) -> None:  # noqa: N802 - stdlib API name
        try:
            super().doRollover()
        except PermissionError:
            # Keep logging to the active file and retry on the next rollover.
            pass


class DropAwareQueueHandler(QueueHandler):
    """Never block producers; synchronously preserve saturated warnings/errors."""

    def __init__(
        self,
        log_queue: queue.Queue,
        fallback_handlers: Iterable[logging.Handler],
    ) -> None:
        super().__init__(log_queue)
        self._fallback_handlers = tuple(fallback_handlers)
        self._drop_lock = threading.Lock()
        self._dropped_by_level: dict[str, int] = defaultdict(int)

    def prepare(self, record: logging.LogRecord) -> logging.LogRecord:
        # QueueHandler.prepare() formats on the producer thread. The queue is
        # in-process, so a shallow copy is sufficient and leaves formatting,
        # traceback rendering, and file I/O to the listener thread.
        return copy.copy(record)

    def enqueue(self, record: logging.LogRecord) -> None:
        try:
            self.queue.put_nowait(record)
        except queue.Full:
            with self._drop_lock:
                self._dropped_by_level[record.levelname] += 1

            # Debug/info records are deliberately lossy under overload. Preserve
            # warning/error diagnostics once through the normal sinks, even though
            # that exceptional path may block on local I/O.
            if record.levelno >= logging.WARNING:
                for handler in self._fallback_handlers:
                    if record.levelno >= handler.level:
                        try:
                            handler.handle(record)
                        except Exception:
                            self.handleError(record)

    @property
    def dropped_by_level(self) -> dict[str, int]:
        with self._drop_lock:
            return dict(self._dropped_by_level)

    @property
    def dropped_total(self) -> int:
        with self._drop_lock:
            return sum(self._dropped_by_level.values())


class BoundedQueueListener(QueueListener):
    """QueueListener with a finite shutdown/drain wait."""

    def stop(self, timeout_seconds: float) -> bool:
        thread = self._thread
        if thread is None:
            return True

        deadline = time.monotonic() + max(0.0, timeout_seconds)
        try:
            self.queue.put(
                self._sentinel,
                timeout=max(0.0, deadline - time.monotonic()),
            )
        except queue.Full:
            return False

        thread.join(max(0.0, deadline - time.monotonic()))
        if thread.is_alive():
            return False

        self._thread = None
        return True


class ServerLoggingRuntime:
    """Owns the server logging queue, listener, and output handlers."""

    def __init__(
        self,
        listener: BoundedQueueListener,
        queue_handler: DropAwareQueueHandler,
        sink_handlers: Iterable[logging.Handler],
        shutdown_timeout_seconds: float,
    ) -> None:
        self.listener = listener
        self.queue_handler = queue_handler
        self.sink_handlers = tuple(sink_handlers)
        self.shutdown_timeout_seconds = shutdown_timeout_seconds
        self._stop_lock = threading.Lock()
        self._stopped = False

    @property
    def dropped_by_level(self) -> dict[str, int]:
        return self.queue_handler.dropped_by_level

    @property
    def dropped_total(self) -> int:
        return self.queue_handler.dropped_total

    def stop(self, timeout_seconds: float | None = None) -> bool:
        with self._stop_lock:
            if self._stopped:
                return True

            timeout = (
                self.shutdown_timeout_seconds
                if timeout_seconds is None
                else max(0.0, timeout_seconds)
            )
            if not self.listener.stop(timeout):
                return False

            root_logger = logging.getLogger()
            if self.queue_handler in root_logger.handlers:
                root_logger.removeHandler(self.queue_handler)

            dropped = self.dropped_by_level
            if dropped:
                details = ", ".join(
                    f"{level.lower()}={count}"
                    for level, count in sorted(dropped.items())
                )
                summary = logging.LogRecord(
                    name="mcp-for-unity-server",
                    level=logging.WARNING,
                    pathname=__file__,
                    lineno=0,
                    msg="Logging queue dropped records under saturation: %s",
                    args=(details,),
                    exc_info=None,
                )
                for handler in self.sink_handlers:
                    if summary.levelno >= handler.level:
                        try:
                            handler.handle(summary)
                        except Exception:
                            pass

            for handler in self.sink_handlers:
                try:
                    handler.flush()
                except Exception:
                    pass
                try:
                    handler.close()
                except Exception:
                    pass

            self._stopped = True
            return True


_active_runtime: ServerLoggingRuntime | None = None
_active_runtime_lock = threading.Lock()


def _positive_int_env(name: str, default: int) -> int:
    try:
        return max(1, int(os.environ.get(name, str(default))))
    except (TypeError, ValueError):
        return default


def _positive_float_env(name: str, default: float) -> float:
    try:
        return max(0.0, float(os.environ.get(name, str(default))))
    except (TypeError, ValueError):
        return default


def _write_setup_failure(stream: IO[str], message: str, exc: Exception) -> None:
    try:
        stream.write(f"MCP logging setup warning: {message}: {exc}\n")
        stream.flush()
    except Exception:
        pass


def configure_server_logging(
    config,
    *,
    log_dir: str | os.PathLike[str] | None = None,
    stream: IO[str] | None = None,
    queue_capacity: int | None = None,
    shutdown_timeout_seconds: float | None = None,
) -> ServerLoggingRuntime:
    """Install one queue-backed root pipeline for stderr and rotating-file logs."""

    global _active_runtime

    with _active_runtime_lock:
        previous = _active_runtime
        _active_runtime = None
    if previous is not None:
        previous.stop()

    output_stream = stream if stream is not None else sys.stderr
    level = getattr(logging, str(config.log_level).upper(), logging.INFO)
    formatter = logging.Formatter(config.log_format)

    stderr_handler = logging.StreamHandler(output_stream)
    stderr_handler.setLevel(level)
    stderr_handler.setFormatter(formatter)
    sink_handlers: list[logging.Handler] = [stderr_handler]

    resolved_log_dir = os.fspath(log_dir) if log_dir is not None else resolve_log_dir()
    try:
        os.makedirs(resolved_log_dir, exist_ok=True)
        file_handler = WindowsSafeRotatingFileHandler(
            os.path.join(resolved_log_dir, "unity_mcp_server.log"),
            maxBytes=512 * 1024,
            backupCount=2,
            encoding="utf-8",
        )
        file_handler.setLevel(level)
        file_handler.setFormatter(formatter)
        sink_handlers.append(file_handler)
    except Exception as exc:
        _write_setup_failure(output_stream, "file logging is unavailable", exc)

    capacity = queue_capacity or _positive_int_env(
        "UNITY_MCP_LOG_QUEUE_CAPACITY",
        config.log_queue_capacity,
    )
    timeout = (
        shutdown_timeout_seconds
        if shutdown_timeout_seconds is not None
        else _positive_float_env(
            "UNITY_MCP_LOG_SHUTDOWN_TIMEOUT_SECONDS",
            config.log_shutdown_timeout_seconds,
        )
    )
    log_queue: queue.Queue = queue.Queue(maxsize=max(1, capacity))
    queue_handler = DropAwareQueueHandler(log_queue, sink_handlers)
    queue_handler.setLevel(level)
    listener = BoundedQueueListener(
        log_queue,
        *sink_handlers,
        respect_handler_level=True,
    )
    runtime = ServerLoggingRuntime(
        listener,
        queue_handler,
        sink_handlers,
        max(0.0, timeout),
    )

    root_logger = logging.getLogger()
    root_logger.handlers.clear()
    root_logger.setLevel(level)
    root_logger.addHandler(queue_handler)

    for logger_name in _APPLICATION_LOGGERS:
        application_logger = logging.getLogger(logger_name)
        application_logger.handlers.clear()
        application_logger.setLevel(level)
        application_logger.propagate = True

    for logger_name in _NOISY_LOGGERS:
        noisy_logger = logging.getLogger(logger_name)
        noisy_logger.handlers.clear()
        noisy_logger.setLevel(max(logging.WARNING, level))
        noisy_logger.propagate = True

    listener.start()
    with _active_runtime_lock:
        _active_runtime = runtime
    return runtime


def shutdown_server_logging(timeout_seconds: float | None = None) -> bool:
    """Drain and stop the active server logging pipeline, if configured."""

    global _active_runtime
    with _active_runtime_lock:
        runtime = _active_runtime
        _active_runtime = None
    if runtime is None:
        return True
    return runtime.stop(timeout_seconds)


atexit.register(shutdown_server_logging)
