from __future__ import annotations

import argparse
import json
import logging
import os
import sys
import time
import uuid

from process_supervisor.state import LaunchState, write_state

logger = logging.getLogger("mcp-for-unity-supervisor")


def classify_server_exit(hard_limit_bytes: int, peak_job_memory_bytes: int) -> str:
    if (
        hard_limit_bytes > 0
        and peak_job_memory_bytes >= int(hard_limit_bytes * 0.95)
    ):
        return "memory_limit_exceeded"
    return "server_exited"


def _log(event: str, **fields) -> None:
    logger.info(
        json.dumps(
            {
                "component": "mcp-for-unity-supervisor",
                "event": event,
                "timestamp": time.time(),
                **fields,
            },
            sort_keys=True,
        )
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Supervise a local MCP server")
    parser.add_argument("--parent-pid", type=int, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--state-file", required=True)
    parser.add_argument("--instance-token", required=True)
    parser.add_argument("--runtime-version", default="unknown")
    parser.add_argument("--soft-memory-limit-mb", type=int, default=512)
    parser.add_argument("--hard-memory-limit-mb", type=int, default=0)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    return parser


def main() -> int:
    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(message)s")
    if os.name != "nt":
        logger.error("mcp-for-unity-supervisor is only required on Windows")
        return 2

    args = build_parser().parse_args()
    command = args.command
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        logger.error("Missing server command after --")
        return 2
    if args.parent_pid <= 1 or args.parent_pid == os.getpid():
        logger.error("Invalid Unity parent PID")
        return 2

    from process_supervisor.windows_job import (
        WindowsJob,
        close_handle,
        open_process_for_wait,
        process_has_exited,
    )

    soft_bytes = max(0, args.soft_memory_limit_mb) * 1024 * 1024
    hard_bytes = max(0, args.hard_memory_limit_mb) * 1024 * 1024
    job_name = f"Local\\MCPForUnity-{args.port}-{uuid.uuid4().hex}"
    parent_handle = open_process_for_wait(args.parent_pid)
    state: LaunchState | None = None
    warned = False

    try:
        with WindowsJob(job_name, hard_bytes) as job:
            server_pid = job.launch_suspended(command)
            state = LaunchState(
                schema_version=1,
                supervisor_pid=os.getpid(),
                server_pid=server_pid,
                unity_pid=args.parent_pid,
                port=args.port,
                instance_token=args.instance_token,
                job_name=job_name,
                soft_memory_limit_bytes=soft_bytes,
                hard_memory_limit_bytes=hard_bytes,
                runtime_version=args.runtime_version,
                launched_at_unix=time.time(),
            )
            write_state(args.state_file, state)
            _log(
                "server_started",
                supervisor_pid=os.getpid(),
                server_pid=server_pid,
                unity_pid=args.parent_pid,
                port=args.port,
                hard_memory_limit_bytes=hard_bytes,
            )

            while True:
                if process_has_exited(parent_handle):
                    state.exit_reason = "unity_parent_exited"
                    _log("unity_parent_exited", unity_pid=args.parent_pid)
                    job.terminate(1)
                    job.wait(5000)
                    break
                if job.wait(0):
                    state.server_exit_code = job.exit_code()
                    state.exit_reason = "server_exited"
                    _log("server_exited", exit_code=state.server_exit_code)
                    break

                accounting = job.accounting()
                state.active_processes = accounting.active_processes
                state.current_private_bytes = accounting.current_private_bytes
                state.peak_job_memory_bytes = accounting.peak_job_memory_bytes
                if soft_bytes and accounting.current_private_bytes >= soft_bytes and not warned:
                    warned = True
                    _log(
                        "soft_memory_limit_exceeded",
                        current_private_bytes=accounting.current_private_bytes,
                        soft_memory_limit_bytes=soft_bytes,
                    )
                write_state(args.state_file, state)
                time.sleep(5)

            try:
                accounting = job.accounting()
                state.active_processes = accounting.active_processes
                state.current_private_bytes = accounting.current_private_bytes
                state.peak_job_memory_bytes = accounting.peak_job_memory_bytes
            except OSError:
                state.active_processes = 0
            if state.exit_reason == "server_exited":
                state.exit_reason = classify_server_exit(
                    hard_bytes,
                    state.peak_job_memory_bytes,
                )
            if state.exit_reason == "memory_limit_exceeded":
                _log(
                    "memory_limit_exceeded",
                    peak_job_memory_bytes=state.peak_job_memory_bytes,
                    hard_memory_limit_bytes=hard_bytes,
                    exit_code=state.server_exit_code,
                )
            write_state(args.state_file, state)
            return state.server_exit_code or 0
    except BaseException as exc:
        if state is not None:
            state.exit_reason = f"supervisor_error:{type(exc).__name__}"
            try:
                write_state(args.state_file, state)
            except OSError:
                pass
        _log("supervisor_error", error=str(exc), error_type=type(exc).__name__)
        raise
    finally:
        close_handle(parent_handle)


if __name__ == "__main__":
    raise SystemExit(main())
