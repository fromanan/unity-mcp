using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using MCPForUnity.Editor.Constants;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    public static class ServerRunStateReader
    {
        private const long MaxLifecycleLogBytes = 1024 * 1024;
        private static readonly object LifecycleLogLock = new();

        public static bool TryReadLast(out ManagedServerStatus status)
        {
            status = null;
            try
            {
                string path = EditorPrefs.GetString(
                    EditorPrefKeys.ForCurrentProject(
                        EditorPrefKeys.LastLocalHttpServerStateFilePath),
                    string.Empty);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return false;
                }
                if (!TryReadPath(path, out status))
                {
                    return false;
                }
                TryReadLiveSessions(status);
                return true;
            }
            catch (Exception)
            {
                status = null;
                return false;
            }
        }

        internal static string GetStateFilePathForPort(int port)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(
                projectRoot,
                "Library",
                "MCPForUnity",
                "RunState",
                $"mcp_http_{port}.state.json");
        }

        internal static string GetLifecycleLogPathForPort(int port)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(
                projectRoot,
                "Library",
                "MCPForUnity",
                "Logs",
                $"server-lifecycle-{port}.jsonl");
        }

        internal static string BuildGeneration(ManagedServerStatus status)
        {
            if (status == null)
            {
                return "unavailable";
            }
            return status.SupervisorPid.ToString(CultureInfo.InvariantCulture)
                   + ":"
                   + status.LaunchedAtUnix.ToString("R", CultureInfo.InvariantCulture);
        }

        internal static string BuildLifecycleEventLine(
            ManagedServerStatus status,
            string eventName,
            string reason,
            int targetProcessId,
            DateTimeOffset timestampUtc,
            int? observedSupervisorExitCode = null,
            long unityPrivateBytes = -1,
            int systemCommitUsedPercent = -1,
            long systemAvailablePhysicalBytes = -1,
            long systemCommitUsedBytes = -1,
            long systemCommitLimitBytes = -1)
        {
            JObject lifecycleEvent = new JObject
            {
                ["schema_version"] = 1,
                ["timestamp_utc"] = timestampUtc.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                ["event"] = eventName,
                ["generation"] = BuildGeneration(status),
                ["reason"] = string.IsNullOrWhiteSpace(reason)
                    ? JValue.CreateNull()
                    : JToken.FromObject(reason),
                ["target_process_id"] = targetProcessId > 0
                    ? JToken.FromObject(targetProcessId)
                    : JValue.CreateNull(),
                ["supervisor_pid"] = status?.SupervisorPid ?? 0,
                ["server_pid"] = status?.ServerPid ?? 0,
                ["unity_pid"] = status?.UnityPid ?? 0,
                ["port"] = status?.Port ?? 0,
                ["launched_at_unix"] = status?.LaunchedAtUnix ?? 0,
                ["active_processes"] = status?.ActiveProcesses ?? 0,
                ["current_private_bytes"] = status?.CurrentPrivateBytes ?? 0,
                ["peak_job_memory_bytes"] = status?.PeakJobMemoryBytes ?? 0,
                ["soft_memory_limit_bytes"] = status?.SoftMemoryLimitBytes ?? 0,
                ["hard_memory_limit_bytes"] = status?.HardMemoryLimitBytes ?? 0,
                ["exit_reason"] = string.IsNullOrWhiteSpace(status?.ExitReason)
                    ? JValue.CreateNull()
                    : JToken.FromObject(status.ExitReason),
                ["server_exit_code"] = status?.ServerExitCode.HasValue == true
                    ? JToken.FromObject(status.ServerExitCode.Value)
                    : JValue.CreateNull(),
                ["observed_supervisor_exit_code"] = observedSupervisorExitCode.HasValue
                    ? JToken.FromObject(observedSupervisorExitCode.Value)
                    : JValue.CreateNull(),
                ["unity_private_bytes"] = unityPrivateBytes >= 0
                    ? JToken.FromObject(unityPrivateBytes)
                    : JValue.CreateNull(),
                ["system_commit_used_percent"] = systemCommitUsedPercent >= 0
                    ? JToken.FromObject(systemCommitUsedPercent)
                    : JValue.CreateNull(),
                ["system_available_physical_bytes"] = systemAvailablePhysicalBytes >= 0
                    ? JToken.FromObject(systemAvailablePhysicalBytes)
                    : JValue.CreateNull(),
                ["system_commit_used_bytes"] = systemCommitUsedBytes >= 0
                    ? JToken.FromObject(systemCommitUsedBytes)
                    : JValue.CreateNull(),
                ["system_commit_limit_bytes"] = systemCommitLimitBytes >= 0
                    ? JToken.FromObject(systemCommitLimitBytes)
                    : JValue.CreateNull()
            };
            return lifecycleEvent.ToString(Formatting.None);
        }

        internal static bool TryAppendLifecycleEvent(
            ManagedServerStatus status,
            string eventName,
            string reason,
            int targetProcessId,
            out string lifecycleLogPath,
            int? observedSupervisorExitCode = null,
            long unityPrivateBytes = -1,
            int systemCommitUsedPercent = -1,
            long systemAvailablePhysicalBytes = -1,
            long systemCommitUsedBytes = -1,
            long systemCommitLimitBytes = -1)
        {
            lifecycleLogPath = status?.Port > 0
                ? GetLifecycleLogPathForPort(status.Port)
                : null;
            if (string.IsNullOrWhiteSpace(lifecycleLogPath))
            {
                return false;
            }

            try
            {
                string line = BuildLifecycleEventLine(
                    status,
                    eventName,
                    reason,
                    targetProcessId,
                    DateTimeOffset.UtcNow,
                    observedSupervisorExitCode,
                    unityPrivateBytes,
                    systemCommitUsedPercent,
                    systemAvailablePhysicalBytes,
                    systemCommitUsedBytes,
                    systemCommitLimitBytes) + Environment.NewLine;
                byte[] bytes = new UTF8Encoding(false).GetBytes(line);
                lock (LifecycleLogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(lifecycleLogPath));
                    long currentLength = File.Exists(lifecycleLogPath)
                        ? new FileInfo(lifecycleLogPath).Length
                        : 0;
                    if (currentLength + bytes.Length > MaxLifecycleLogBytes)
                    {
                        File.Copy(
                            lifecycleLogPath,
                            lifecycleLogPath + ".previous",
                            overwrite: true);
                        File.WriteAllBytes(lifecycleLogPath, bytes);
                    }
                    else
                    {
                        using FileStream stream = new FileStream(
                            lifecycleLogPath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool HasRecentShutdownRequest(
            ManagedServerStatus status,
            DateTimeOffset nowUtc,
            TimeSpan maximumAge)
        {
            if (status?.Port <= 0)
            {
                return false;
            }

            string lifecycleLogPath = GetLifecycleLogPathForPort(status.Port);
            return HasRecentShutdownRequest(
                lifecycleLogPath,
                status,
                nowUtc,
                maximumAge);
        }

        internal static bool HasRecentShutdownRequest(
            string lifecycleLogPath,
            ManagedServerStatus status,
            DateTimeOffset nowUtc,
            TimeSpan maximumAge)
        {
            if (!File.Exists(lifecycleLogPath))
            {
                return false;
            }

            string generation = BuildGeneration(status);
            DateTimeOffset cutoff = nowUtc.ToUniversalTime() - maximumAge;
            try
            {
                foreach (string line in File.ReadLines(lifecycleLogPath))
                {
                    JObject lifecycleEvent;
                    try
                    {
                        lifecycleEvent = JObject.Parse(line);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!string.Equals(
                            lifecycleEvent.Value<string>("event"),
                            "shutdown_requested",
                            StringComparison.Ordinal)
                        || !string.Equals(
                            lifecycleEvent.Value<string>("generation"),
                            generation,
                            StringComparison.Ordinal)
                        || !TryReadTimestampUtc(
                            lifecycleEvent["timestamp_utc"],
                            out DateTimeOffset timestampUtc))
                    {
                        continue;
                    }

                    if (timestampUtc >= cutoff && timestampUtc <= nowUtc.ToUniversalTime())
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TryReadTimestampUtc(
            JToken timestampToken,
            out DateTimeOffset timestampUtc)
        {
            timestampUtc = default;
            if (timestampToken == null || timestampToken.Type == JTokenType.Null)
            {
                return false;
            }

            if (timestampToken.Type == JTokenType.Date)
            {
                try
                {
                    timestampUtc = timestampToken
                        .ToObject<DateTimeOffset>()
                        .ToUniversalTime();
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return DateTimeOffset.TryParse(
                timestampToken.Value<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestampUtc);
        }

        internal static bool TryReadPath(string path, out ManagedServerStatus status)
        {
            status = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return false;
                }
                JObject json = JObject.Parse(File.ReadAllText(path));
                if (json.Value<int?>("schema_version") != 1)
                {
                    return false;
                }
                status = new ManagedServerStatus
                {
                    SupervisorPid = json.Value<int?>("supervisor_pid") ?? 0,
                    ServerPid = json.Value<int?>("server_pid") ?? 0,
                    UnityPid = json.Value<int?>("unity_pid") ?? 0,
                    Port = json.Value<int?>("port") ?? 0,
                    ActiveProcesses = json.Value<int?>("active_processes") ?? 0,
                    CurrentPrivateBytes = json.Value<long?>("current_private_bytes") ?? 0,
                    PeakJobMemoryBytes = json.Value<long?>("peak_job_memory_bytes") ?? 0,
                    SoftMemoryLimitBytes = json.Value<long?>("soft_memory_limit_bytes") ?? 0,
                    HardMemoryLimitBytes = json.Value<long?>("hard_memory_limit_bytes") ?? 0,
                    RuntimeVersion = json.Value<string>("runtime_version") ?? "unknown",
                    LaunchedAtUnix = json.Value<double?>("launched_at_unix") ?? 0,
                    ExitReason = json.Value<string>("exit_reason"),
                    ServerExitCode = json.Value<int?>("server_exit_code")
                };
                return true;
            }
            catch (Exception)
            {
                status = null;
                return false;
            }
        }

        private static void TryReadLiveSessions(ManagedServerStatus status)
        {
            if (status.Port <= 0 || !string.IsNullOrWhiteSpace(status.ExitReason))
            {
                return;
            }
            string token = EditorPrefs.GetString(
                EditorPrefKeys.ForCurrentProject(
                    EditorPrefKeys.LastLocalHttpServerInstanceToken),
                string.Empty);
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(
                    $"http://127.0.0.1:{status.Port}/api/server/status");
                request.Method = "GET";
                request.Timeout = 500;
                request.ReadWriteTimeout = 500;
                request.Headers["X-Unity-MCP-Instance-Token"] = token;
                using var response = (HttpWebResponse)request.GetResponse();
                using var reader = new StreamReader(response.GetResponseStream());
                var payload = JObject.Parse(reader.ReadToEnd());
                var sessions = payload["sessions"] as JObject;
                status.ActiveHttpSessions = sessions?.Value<int?>("active");
                status.MaximumHttpSessions = sessions?.Value<int?>("maximum");
            }
            catch
            {
                // The launch-state file remains useful while the server starts
                // or after it exits, even when live HTTP metrics are unavailable.
            }
        }
    }
}
