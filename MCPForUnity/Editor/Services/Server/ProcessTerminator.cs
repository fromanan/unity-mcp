using System;
using System.IO;
using System.Threading;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>
    /// Platform-specific process termination for stopping MCP server processes.
    /// </summary>
    public class ProcessTerminator : IProcessTerminator
    {
        private readonly IProcessDetector _processDetector;
        private readonly IProcessCommandRunner _commandRunner;
        private readonly Action<int> _sleep;
        private readonly Func<DateTime> _utcNow;

        /// <summary>
        /// Creates a new ProcessTerminator with the specified process detector.
        /// </summary>
        /// <param name="processDetector">Process detector for checking process existence</param>
        public ProcessTerminator(
            IProcessDetector processDetector,
            IProcessCommandRunner commandRunner = null,
            Action<int> sleep = null,
            Func<DateTime> utcNow = null)
        {
            _processDetector = processDetector ?? throw new ArgumentNullException(nameof(processDetector));
            _commandRunner = commandRunner ?? new ProcessCommandRunner();
            _sleep = sleep ?? Thread.Sleep;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <inheritdoc/>
        public bool Terminate(int pid, int? expectedPort = null)
        {
            // CRITICAL: Validate PID before any kill operation.
            // On Unix, kill(-1) kills ALL processes the user can signal!
            // On Unix, kill(0) signals all processes in the process group.
            // PID 1 is init/launchd and must never be killed.
            // Only positive PIDs > 1 are valid for targeted termination.
            if (pid <= 1)
            {
                return false;
            }

            // Never kill the current Unity process
            int currentPid = _processDetector.GetCurrentProcessId();
            if (currentPid > 0 && pid == currentPid)
            {
                return false;
            }

            if (!_processDetector.ProcessExists(pid))
            {
                return false;
            }

            try
            {
                string stdout, stderr;
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // taskkill exit code only describes the command invocation. Always
                    // verify process death and port release before reporting success.
                    _commandRunner.Run("taskkill", $"/PID {pid} /T", out stdout, out stderr);
                    if (WaitUntilStopped(pid, expectedPort, TimeSpan.FromSeconds(3)))
                    {
                        return true;
                    }
                    _commandRunner.Run("taskkill", $"/F /PID {pid} /T", out stdout, out stderr);
                    return WaitUntilStopped(pid, expectedPort, TimeSpan.FromSeconds(8));
                }
                else
                {
                    // Try a graceful termination first, then escalate if the process is still alive.
                    // Note: `kill -15` can succeed (exit 0) even if the process takes time to exit,
                    // so we verify and only escalate when needed.
                    string killPath = "/bin/kill";
                    if (!File.Exists(killPath)) killPath = "kill";
                    _commandRunner.Run(killPath, $"-15 {pid}", out stdout, out stderr);
                    if (WaitUntilStopped(pid, expectedPort, TimeSpan.FromSeconds(8)))
                    {
                        return true;
                    }

                    _commandRunner.Run(killPath, $"-9 {pid}", out stdout, out stderr);
                    return WaitUntilStopped(pid, expectedPort, TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error killing process {pid}: {ex.Message}");
                return false;
            }
        }

        private bool WaitUntilStopped(int pid, int? expectedPort, TimeSpan timeout)
        {
            DateTime deadline = _utcNow() + timeout;
            do
            {
                bool processGone = !_processDetector.ProcessExists(pid);
                bool portReleased = !expectedPort.HasValue
                    || expectedPort.Value <= 0
                    || _processDetector.GetListeningProcessIdsForPort(expectedPort.Value).Count == 0;
                if (processGone && portReleased)
                {
                    return true;
                }
                _sleep(100);
            }
            while (_utcNow() < deadline);

            return false;
        }
    }
}
