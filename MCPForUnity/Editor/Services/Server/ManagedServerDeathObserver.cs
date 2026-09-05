using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    [InitializeOnLoad]
    internal static class ManagedServerDeathObserver
    {
        private const double PollIntervalSeconds = 5.0;
        private const int MissingPollsBeforeReport = 2;
        private const string ReportedGenerationSessionKey =
            "MCPForUnity.ManagedServerDeathObserver.ReportedGeneration";
        private static readonly int CurrentUnityPid = GetCurrentUnityPid();
        private static readonly double CurrentUnityStartedAtUnix =
            GetCurrentUnityStartedAtUnix();
        private static double _nextPollAt;
        private static string _generation;
        private static int _consecutiveMissing;

        static ManagedServerDeathObserver()
        {
            if (!ShouldRunObserver(
                    Application.isBatchMode,
                    Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH")))
            {
                return;
            }

            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        internal static bool ShouldRunObserver(bool isBatchMode, string allowBatchEnv)
        {
            return !isBatchMode || !string.IsNullOrWhiteSpace(allowBatchEnv);
        }

        internal static bool ShouldReportUnexpectedExit(
            ManagedServerStatus status,
            int currentUnityPid,
            double currentUnityStartedAtUnix,
            bool supervisorAlive,
            int consecutiveMissing,
            bool alreadyReported)
        {
            if (status == null ||
                status.SupervisorPid <= 0 ||
                status.UnityPid != currentUnityPid ||
                supervisorAlive ||
                consecutiveMissing < MissingPollsBeforeReport ||
                alreadyReported ||
                !string.IsNullOrWhiteSpace(status.ExitReason))
            {
                return false;
            }

            return currentUnityStartedAtUnix <= 0 ||
                   status.LaunchedAtUnix >= currentUnityStartedAtUnix - 2.0;
        }

        private static void Poll()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextPollAt)
            {
                return;
            }
            _nextPollAt = now + PollIntervalSeconds;

            try
            {
                string baseUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (!HttpEndpointUtility.IsHttpLocalUrlAllowedForLaunch(baseUrl, out _) ||
                    !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri) ||
                    uri.Port <= 0)
                {
                    return;
                }

                string stateFilePath = ServerRunStateReader.GetStateFilePathForPort(uri.Port);
                if (!ServerRunStateReader.TryReadPath(
                        stateFilePath,
                        out ManagedServerStatus status))
                {
                    return;
                }

                string generation = BuildGeneration(status);
                if (!string.Equals(_generation, generation, StringComparison.Ordinal))
                {
                    _generation = generation;
                    _consecutiveMissing = 0;
                }

                bool supervisorAlive = IsExpectedProcessAlive(
                    status.SupervisorPid,
                    status.LaunchedAtUnix);
                if (supervisorAlive || !string.IsNullOrWhiteSpace(status.ExitReason))
                {
                    _consecutiveMissing = 0;
                    return;
                }

                _consecutiveMissing++;
                string reportedGeneration = SessionState.GetString(
                    ReportedGenerationSessionKey,
                    string.Empty);
                bool alreadyReported = string.Equals(
                    reportedGeneration,
                    generation,
                    StringComparison.Ordinal);
                if (!ShouldReportUnexpectedExit(
                        status,
                        CurrentUnityPid,
                        CurrentUnityStartedAtUnix,
                        supervisorAlive,
                        _consecutiveMissing,
                        alreadyReported))
                {
                    return;
                }

                WriteUnexpectedExitDiagnostic(stateFilePath, status);
                SessionState.SetString(ReportedGenerationSessionKey, generation);
            }
            catch (Exception)
            {
                // Diagnostics must never interfere with the Unity Editor update loop.
            }
        }

        private static bool IsExpectedProcessAlive(int processId, double launchedAtUnix)
        {
            if (processId <= 0)
            {
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                    {
                        return false;
                    }

                    if (launchedAtUnix <= 0)
                    {
                        return true;
                    }

                    double processStartedAtUnix =
                        new DateTimeOffset(process.StartTime.ToUniversalTime())
                            .ToUnixTimeMilliseconds() / 1000.0;
                    return processStartedAtUnix <= launchedAtUnix + 5.0 &&
                           processStartedAtUnix >= launchedAtUnix - 60.0;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static void WriteUnexpectedExitDiagnostic(
            string stateFilePath,
            ManagedServerStatus status)
        {
            DateTime stateLastWriteUtc = File.Exists(stateFilePath)
                ? File.GetLastWriteTimeUtc(stateFilePath)
                : DateTime.MinValue;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string logDirectory = Path.Combine(
                projectRoot,
                "Library",
                "MCPForUnity",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            string diagnosticPath = Path.Combine(
                logDirectory,
                $"server-lifecycle-{status.Port}.jsonl");

            JObject diagnostic = new JObject
            {
                ["schema_version"] = 1,
                ["timestamp_utc"] = DateTimeOffset.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                ["event"] = "supervisor_disappeared_unclassified",
                ["supervisor_pid"] = status.SupervisorPid,
                ["server_pid"] = status.ServerPid,
                ["unity_pid"] = status.UnityPid,
                ["port"] = status.Port,
                ["launched_at_unix"] = status.LaunchedAtUnix,
                ["state_last_write_utc"] = stateLastWriteUtc == DateTime.MinValue
                    ? JValue.CreateNull()
                    : JToken.FromObject(stateLastWriteUtc.ToString(
                        "O",
                        CultureInfo.InvariantCulture)),
                ["active_processes"] = status.ActiveProcesses,
                ["current_private_bytes"] = status.CurrentPrivateBytes,
                ["peak_job_memory_bytes"] = status.PeakJobMemoryBytes,
                ["soft_memory_limit_bytes"] = status.SoftMemoryLimitBytes,
                ["hard_memory_limit_bytes"] = status.HardMemoryLimitBytes,
                ["exit_reason"] = string.IsNullOrWhiteSpace(status.ExitReason)
                    ? JValue.CreateNull()
                    : JToken.FromObject(status.ExitReason),
                ["server_exit_code"] = status.ServerExitCode.HasValue
                    ? JToken.FromObject(status.ServerExitCode.Value)
                    : JValue.CreateNull()
            };
            File.AppendAllText(
                diagnosticPath,
                diagnostic.ToString(Formatting.None) + Environment.NewLine);
            McpLog.Warn(
                $"Managed MCP supervisor PID {status.SupervisorPid} disappeared without " +
                $"recording an exit reason. Diagnostic: {diagnosticPath}");
        }

        private static string BuildGeneration(ManagedServerStatus status)
        {
            return status.SupervisorPid.ToString(CultureInfo.InvariantCulture) +
                   ":" +
                   status.LaunchedAtUnix.ToString("R", CultureInfo.InvariantCulture);
        }

        private static int GetCurrentUnityPid()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return process.Id;
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static double GetCurrentUnityStartedAtUnix()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return new DateTimeOffset(process.StartTime.ToUniversalTime())
                        .ToUnixTimeMilliseconds() / 1000.0;
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
