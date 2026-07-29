using System;
using System.IO;
using System.Net;
using MCPForUnity.Editor.Constants;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services.Server
{
    public static class ServerRunStateReader
    {
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
                var json = JObject.Parse(File.ReadAllText(path));
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
                TryReadLiveSessions(status);
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
