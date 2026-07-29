# Managed Local Server Lifecycle

The Unity package owns the local HTTP server as a project-scoped runtime. `uv`
is used to create and update that runtime, then exits; it is not an ancestor of
the steady-state server.

## Launch architecture

```text
Unity Editor
  -> ServerRuntimeInstaller
       -> uv venv / uv pip install (short-lived)
       -> Library/MCPForUnity/ServerRuntime/<version-source-hash>/
  -> mcp-for-unity-supervisor.exe (Windows)
       -> Windows Job Object
            -> mcp-for-unity.exe
                 -> Python MCP HTTP server
```

On Windows, the installer requests a uv-managed Python 3.12 runtime. Microsoft
Store Python redirectors may break the real interpreter out of a Job Object;
the uv-managed interpreter keeps the complete server process tree contained so
job accounting and hard memory limits apply to the actual Python process.

The installer builds in a staging directory, installs the requested package
source, probes Python and both entry points, writes `runtime.json`, and only
then promotes the runtime. Existing version/source directories remain
available as known-good runtimes.

## Session bounds

The local HTTP server uses an isolated FastMCP compatibility adapter that:

- expires idle sessions after 1,800 seconds by default;
- allows at most 64 active sessions by default;
- returns HTTP 503 with `Retry-After` when admission is full;
- removes explicit `DELETE` sessions from FastMCP's retained transport map;
- reports created, deleted, expired/closed, rejected, and active counts.

FastMCP 3.4.5 and MCP SDK 1.29.0 are pinned while this adapter relies on their
internal session-manager API. Upgrade them only with the bounded-session tests,
packaged-runtime probe, and real HTTP lifecycle smoke test.

The limits are available as `--http-session-idle-timeout` and
`--http-max-sessions`, with matching `UNITY_MCP_HTTP_*` environment variables
and Unity Advanced Settings.

## Windows containment and memory policy

The supervisor creates the server suspended, assigns it to a Job Object, and
resumes it only after assignment succeeds. The job always enables
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.

- Soft warning default: 512 MiB of current private bytes.
- Hard limit default: disabled.
- Suggested opt-in hard-limit setting: 768 MiB.

The hard limit is a circuit breaker, not a leak fix. When job peak memory
reaches the configured ceiling and the server exits, launch state records
`memory_limit_exceeded`.

The supervisor samples every five seconds and writes an atomic state file under
`Library/MCPForUnity/RunState`. The Advanced Settings UI shows runtime version,
supervisor/server PIDs, process count, current and peak memory, live HTTP
sessions when available, the hard-limit state, and the final exit reason.

## Shutdown

Normal shutdown is:

1. Unity calls loopback-only `POST /api/shutdown` with the per-launch instance
   token.
2. The explicit Uvicorn server begins graceful shutdown.
3. Unity verifies both process exit and port release.
4. If verification fails, Unity escalates to verified process-tree
   termination.
5. Tracking is cleared only after the listener is gone.

If Unity exits abruptly, the supervisor observes the Unity process handle and
terminates the Job Object. Closing the supervisor's job handle also kills all
remaining descendants.

## Relevant implementation

- `MCPForUnity/Editor/Services/Server/ServerRuntimeInstaller.cs`
- `MCPForUnity/Editor/Services/Server/ServerCommandBuilder.cs`
- `MCPForUnity/Editor/Services/ServerManagementService.cs`
- `MCPForUnity/Editor/Services/Server/ProcessTerminator.cs`
- `Server/src/http_runtime.py`
- `Server/src/transport/bounded_streamable_http.py`
- `Server/src/process_supervisor/`

The standalone profiling harness described in
`.ai/server-memory-lifecycle-hardening-plan.md` is intentionally deferred.
