import logging
import threading
import time

import core.telemetry as telemetry


def test_telemetry_queue_backpressure_and_single_worker(monkeypatch, caplog, tmp_path):
    # Directly attach caplog's handler to the telemetry logger so that
    # earlier tests calling logging.basicConfig() can't steal the records
    # via a root handler before caplog sees them.
    tel_logger = logging.getLogger("unity-mcp-telemetry")
    tel_logger.addHandler(caplog.handler)
    try:
        caplog.set_level("DEBUG", logger="unity-mcp-telemetry")

        monkeypatch.setenv("APPDATA", str(tmp_path))
        monkeypatch.setattr(telemetry, "_QUEUE_MAX_RECORDS", 2)
        monkeypatch.setattr(
            telemetry.TelemetryConfig,
            "_is_disabled",
            lambda self: False,
        )

        # Make sends slow to build backlog and exercise the bounded queue.
        def slow_send(self, rec):
            time.sleep(0.05)

        monkeypatch.setattr(telemetry.TelemetryCollector, "_send_telemetry", slow_send)
        collector = telemetry.TelemetryCollector()
        try:
            # Fire many events quickly; record() should not block even when queue fills
            start = time.perf_counter()
            for i in range(50):
                collector.record(telemetry.RecordType.TOOL_EXECUTION, {"i": i})
            elapsed_ms = (time.perf_counter() - start) * 1000.0

            # Should be fast despite backpressure (non-blocking enqueue or drop)
            # Threshold set high (500ms) to accommodate CI environments with variable load.
            # The key assertion is that 50 record() calls don't block on a full queue;
            # even under heavy CI load, non-blocking calls should complete well under 500ms.
            assert elapsed_ms < 500.0, f"Took {elapsed_ms:.1f}ms (expected <500ms for non-blocking calls)"

            # Allow worker to process some
            time.sleep(0.3)

            # Verify drops were logged (queue full backpressure)
            dropped_logs = [
                m for m in caplog.messages if "Telemetry queue full; dropping" in m]
            assert len(dropped_logs) >= 1

            # Ensure only one worker thread exists and is alive
            assert collector._worker is not None
            assert collector._worker.is_alive()
            worker_threads = [
                t for t in threading.enumerate() if t is collector._worker]
            assert len(worker_threads) == 1

            collector.shutdown()
            assert not collector._worker.is_alive()
            queued_after_shutdown = collector._queue.qsize()
            collector.record(telemetry.RecordType.TOOL_EXECUTION, {"late": True})
            assert collector._queue.qsize() == queued_after_shutdown
        finally:
            collector.shutdown()
    finally:
        if caplog.handler in tel_logger.handlers:
            tel_logger.removeHandler(caplog.handler)
