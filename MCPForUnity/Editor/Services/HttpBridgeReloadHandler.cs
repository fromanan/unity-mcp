using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Server;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Windows;
using UnityEditor;
using UnityEditor.Compilation;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Ensures HTTP transports resume after domain reloads similar to the legacy stdio bridge.
    /// </summary>
    [InitializeOnLoad]
    internal static class HttpBridgeReloadHandler
    {
        // SessionState, not EditorPrefs: it survives domain reloads but dies with the editor
        // process and is per-editor-instance. EditorPrefs is per-user machine-global, so a
        // second open editor could consume or delete this editor's pending resume, and a
        // crash mid-compile would leave a stale flag that resurrects the bridge on the next
        // launch (#1229).
        internal const string ResumeSessionKey = "MCPForUnity.ResumeHttpAfterReload";
        internal const string ResumeWarningSessionKey =
            "MCPForUnity.ResumeHttpAfterReload.WarningIssued";
        private const double PersistentReconnectIntervalSeconds = 30.0;

        private static readonly TimeSpan[] ResumeRetrySchedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        private static bool _persistentReconnectInFlight;
        private static double _nextPersistentReconnectAt;

        static HttpBridgeReloadHandler()
        {
            // Migration: the flag lived in EditorPrefs before it moved to SessionState — the
            // key STRING is shared, so renaming ResumeSessionKey would silently break this
            // cleanup. Once per session, not per reload: the EditorPrefs key is machine-global,
            // and an older-version editor open concurrently still uses it for its own resume.
            // Safe to delete this block a few releases after v10.
            const string migratedKey = "MCPForUnity.ResumeHttpAfterReload.Migrated";
            if (!SessionState.GetBool(migratedKey, false))
            {
                EditorPrefs.DeleteKey(ResumeSessionKey);
                SessionState.SetBool(migratedKey, true);
            }

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        internal static bool IsResumePending => SessionState.GetBool(ResumeSessionKey, false);

        /// <summary>
        /// Drops a pending reload-resume. Called when the user takes manual control of the
        /// bridge lifecycle (Connect, End Session, transport switch, orphan cleanup); the
        /// retry loop re-checks the flag per attempt, so this also aborts an in-flight loop.
        /// </summary>
        internal static void CancelPendingResume()
        {
            SessionState.EraseBool(ResumeSessionKey);
            SessionState.EraseBool(ResumeWarningSessionKey);
            EditorApplication.update -= PersistentResumeTick;
            _persistentReconnectInFlight = false;
            _nextPersistentReconnectAt = 0;
        }

        private static void OnBeforeAssemblyReload()
        {
            try
            {
                OnBeforeAssemblyReloadCore(MCPServiceLocator.TransportManager);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to evaluate HTTP bridge reload state: {ex.Message}");
            }
        }

        internal static void OnBeforeAssemblyReloadCore(TransportManager transport)
        {
            TransportState state = transport.GetState(TransportMode.Http);
            if (state.Phase != TransportPhase.Stopped && state.Phase != TransportPhase.Faulted)
            {
                SessionState.SetBool(ResumeSessionKey, true);

                // beforeAssemblyReload is synchronous. Give the client one bounded chance to
                // publish the planned reload and a normal close frame before the hard fallback.
                try
                {
                    Task drainTask = transport.NotifyLifecycleAsync(TransportMode.Http, "reloading");
                    if (!drainTask.Wait(TimeSpan.FromSeconds(1.5)))
                    {
                        McpLog.Debug("[HTTP Reload] Graceful reload drain timed out; forcing teardown");
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Debug($"[HTTP Reload] Graceful reload drain failed: {ex.Message}");
                }
                transport.ForceStop(TransportMode.Http);
            }
            // When the bridge is not running, leave any pending flag alone: during a multi-pass
            // compile the next reload lands before the deferred resume ran, and deleting the
            // flag here is what used to lose the resume permanently (#1229). Explicit cancel
            // paths (End Session, transport switch, orphan cleanup) erase the flag instead.
        }

        private static void OnCompilationStarted(object context)
        {
            TransportManager transport = MCPServiceLocator.TransportManager;
            if (!transport.IsRunning(TransportMode.Http))
            {
                return;
            }

            _ = NotifyLifecycleBestEffortAsync(transport, "compiling");
        }

        private static void OnCompilationFinished(object context)
        {
            TransportManager transport = MCPServiceLocator.TransportManager;
            TransportState state = transport.GetState(TransportMode.Http);
            if (state.Phase == TransportPhase.Draining)
            {
                _ = NotifyLifecycleBestEffortAsync(transport, "ready");
            }
        }

        private static async Task NotifyLifecycleBestEffortAsync(
            TransportManager transport,
            string lifecycleState)
        {
            try
            {
                await transport.NotifyLifecycleAsync(
                    TransportMode.Http,
                    lifecycleState);
            }
            catch (Exception ex)
            {
                McpLog.Debug(
                    $"[HTTP Reload] Failed to publish '{lifecycleState}' lifecycle state: {ex.Message}");
            }
        }

        private static void OnAfterAssemblyReload()
        {
            if (OnAfterAssemblyReloadCore())
            {
                EditorApplication.update += ResumeTick;
            }
        }

        /// <summary>
        /// Decision core, separated so EditMode tests can drive it. Returns true when a resume
        /// should be scheduled. Does not consume the flag — it survives until the resume
        /// succeeds or is explicitly cancelled, so a further reload in the middle
        /// of any deferral re-enters here instead of losing the resume.
        /// </summary>
        internal static bool OnAfterAssemblyReloadCore()
        {
            try
            {
                if (!SessionState.GetBool(ResumeSessionKey, false)) return false;

                // Only resume HTTP if it is still the selected transport.
                if (!EditorConfigurationCache.Instance.UseHttpTransport)
                {
                    LogTransportConfigurationChanged("after_reload_selection_check");
                    CancelPendingResume();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                // Transport-config read failed (services racing the reload boundary): schedule
                // the resume anyway rather than dropping it — the retry loop re-checks the
                // transport per attempt and retains the flag until connection or an explicit
                // cancel/switch, so a transient config read failure cannot drop reconnect intent.
                McpLog.Warn($"Failed to read HTTP bridge reload flag: {ex.Message}");
                return true;
            }
        }

        private static void ResumeTick()
        {
            if (IsEditorBusy()) return;
            EditorApplication.update -= ResumeTick;
            _ = ResumeHttpWithRetriesAsync();
        }

        /// <summary>
        /// Busy gate for the deferral ticks. Uses the #549-aware compiling check because raw
        /// EditorApplication.isCompiling stays true for a whole play session under the
        /// "Recompile After Finished Playing" preference, which would block resume until
        /// play mode exits.
        /// </summary>
        internal static bool IsEditorBusy()
            => EditorStateCache.GetActualIsCompiling() || EditorApplication.isUpdating;

        // scheduleOverride lets EditMode tests pass an all-zero schedule so the loop
        // completes synchronously (the test framework floor cannot run async tests).
        internal static async Task ResumeHttpWithRetriesAsync(TimeSpan[] scheduleOverride = null)
        {
            TimeSpan[] schedule = scheduleOverride ?? ResumeRetrySchedule;
            Exception lastException = null;
            string lastReason = null;
            bool supervisorArmed = false;

            for (int i = 0; i < schedule.Length; i++)
            {
                int attempt = i + 1;
                McpLog.Debug($"[HTTP Reload] Resume attempt {attempt}/{schedule.Length}");

                TimeSpan delay = schedule[i];
                if (delay > TimeSpan.Zero)
                {
                    McpLog.Debug($"[HTTP Reload] Waiting {delay.TotalSeconds:0.#}s before resume attempt {attempt}");
                    try { await Task.Delay(delay); }
                    catch { return; }
                }

                // The flag doubles as the cancel signal (see CancelPendingResume).
                if (!IsResumePending) return;

                try
                {
                    // Inside the attempt try: a service read racing the reload boundary must
                    // burn a retry, not kill this fire-and-forget task with the flag still set
                    // (which would leave nothing scheduled to consume it until the next reload).

                    // Abort retries if the user switched transports while we were waiting.
                    if (!EditorConfigurationCache.Instance.UseHttpTransport)
                    {
                        LogTransportConfigurationChanged("retry_selection_check");
                        CancelPendingResume();
                        return;
                    }

                    // Never bounce a session someone else established while we were waiting
                    // (WebSocketTransportClient.StartAsync tears down a live connection first).
                    if (MCPServiceLocator.TransportManager.IsRunning(TransportMode.Http))
                    {
                        CompleteResume(attempt);
                        return;
                    }

                    TransportManager transport = MCPServiceLocator.TransportManager;
                    supervisorArmed = await transport.EnsurePersistentReconnectAsync(TransportMode.Http);
                    if (transport.IsRunning(TransportMode.Http))
                    {
                        CompleteResume(attempt);
                        return;
                    }

                    TransportState state = transport.GetState(TransportMode.Http);
                    string reconnectFailure = transport.GetLastReconnectFailure(TransportMode.Http);
                    lastReason = !string.IsNullOrWhiteSpace(reconnectFailure)
                        ? reconnectFailure
                        : state?.Error;
                    string reason = string.IsNullOrWhiteSpace(lastReason)
                        ? "reconnect supervisor armed; connection is not ready"
                        : lastReason;
                    McpLog.Debug($"[HTTP Reload] Resume attempt {attempt} pending: {reason}");
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    lastReason = ex.Message;
                    McpLog.Debug($"[HTTP Reload] Resume attempt {attempt} threw: {ex.Message}");
                }
            }

            TransportManager currentTransport = MCPServiceLocator.TransportManager;
            if (currentTransport.IsRunning(TransportMode.Http))
            {
                CompleteResume(schedule.Length);
                return;
            }

            ArmPersistentResumeMonitor();
            bool supervisorActive = supervisorArmed
                && currentTransport.IsPersistentReconnectActive(TransportMode.Http);
            LogPersistentFailureOnce(
                schedule.Length,
                supervisorActive,
                lastReason ?? lastException?.Message);
        }

        internal static async Task PersistentResumeOnceAsync(TransportManager transport)
        {
            if (!IsResumePending)
            {
                EditorApplication.update -= PersistentResumeTick;
                return;
            }

            if (!EditorConfigurationCache.Instance.UseHttpTransport)
            {
                LogTransportConfigurationChanged("persistent_monitor_selection_check");
                CancelPendingResume();
                return;
            }

            if (transport.IsRunning(TransportMode.Http))
            {
                CompleteResume(0);
                return;
            }

            bool supervisorActive = transport.IsPersistentReconnectActive(TransportMode.Http);
            if (!supervisorActive)
            {
                supervisorActive = await transport.EnsurePersistentReconnectAsync(TransportMode.Http);
            }

            if (transport.IsRunning(TransportMode.Http))
            {
                CompleteResume(0);
                return;
            }

            LogPersistentFailureOnce(
                0,
                supervisorActive,
                transport.GetLastReconnectFailure(TransportMode.Http));
        }

        internal static string ClassifyResumeFailure(
            bool httpSelected,
            bool serverReachable,
            bool supervisorActive,
            string lastReason)
        {
            if (!httpSelected)
            {
                return "transport_configuration_changed";
            }

            string normalizedReason = lastReason?.ToLowerInvariant() ?? string.Empty;
            if (normalizedReason.Contains("handshake")
                || normalizedReason.Contains("registration")
                || normalizedReason.Contains("did not become ready"))
            {
                return "plugin_handshake_timeout";
            }

            if (!supervisorActive && serverReachable)
            {
                return "supervisor_disappeared_unclassified";
            }

            if (!serverReachable
                || normalizedReason.Contains("connection failed")
                || normalizedReason.Contains("actively refused")
                || normalizedReason.Contains("unable to connect"))
            {
                return "server_unreachable_after_reload";
            }

            return supervisorActive
                ? "plugin_handshake_timeout"
                : "supervisor_disappeared_unclassified";
        }

        private static void ArmPersistentResumeMonitor()
        {
            _nextPersistentReconnectAt = EditorApplication.timeSinceStartup
                + PersistentReconnectIntervalSeconds;
            EditorApplication.update -= PersistentResumeTick;
            EditorApplication.update += PersistentResumeTick;
        }

        private static void PersistentResumeTick()
        {
            if (!IsResumePending)
            {
                EditorApplication.update -= PersistentResumeTick;
                return;
            }

            TransportManager transport = MCPServiceLocator.TransportManager;
            if (transport.IsRunning(TransportMode.Http))
            {
                CompleteResume(0);
                return;
            }

            if (_persistentReconnectInFlight
                || IsEditorBusy()
                || EditorApplication.timeSinceStartup < _nextPersistentReconnectAt)
            {
                return;
            }

            _persistentReconnectInFlight = true;
            _nextPersistentReconnectAt = EditorApplication.timeSinceStartup
                + PersistentReconnectIntervalSeconds;
            _ = RunPersistentResumeTickAsync(transport);
        }

        private static async Task RunPersistentResumeTickAsync(TransportManager transport)
        {
            try
            {
                await PersistentResumeOnceAsync(transport);
            }
            catch (Exception ex)
            {
                LogPersistentFailureOnce(
                    0,
                    transport.IsPersistentReconnectActive(TransportMode.Http),
                    ex.Message);
            }
            finally
            {
                _persistentReconnectInFlight = false;
            }
        }

        private static void CompleteResume(int attempt)
        {
            bool warningIssued = SessionState.GetBool(ResumeWarningSessionKey, false);
            CancelPendingResume();
            if (attempt > 0)
            {
                McpLog.Debug($"[HTTP Reload] Resume succeeded on attempt {attempt}");
            }
            if (warningIssued)
            {
                McpLog.Info("[HTTP Reload] Persistent reconnect recovered the MCP bridge");
            }
            MCPForUnityEditorWindow.RequestHealthVerification();
        }

        private static void LogPersistentFailureOnce(
            int attempts,
            bool supervisorActive,
            string lastReason)
        {
            if (SessionState.GetBool(ResumeWarningSessionKey, false))
            {
                return;
            }

            SessionState.SetBool(ResumeWarningSessionKey, true);
            bool serverReachable = IsConfiguredServerReachable();
            string code = ClassifyResumeFailure(
                EditorConfigurationCache.Instance.UseHttpTransport,
                serverReachable,
                supervisorActive,
                lastReason);
            string retryState = supervisorActive
                ? "persistent_reconnect_active_30s_tail"
                : "persistent_reconnect_rearm_30s";
            McpLog.Warn(
                $"[HTTP Reload] code={code} endpoint='{GetSafeEndpoint()}' " +
                $"last_reason='{NormalizeLogField(lastReason)}' attempts={attempts} " +
                $"retry_state={retryState} lifecycle_log='{GetLifecycleLogPath()}'");
        }

        private static void LogTransportConfigurationChanged(string reason)
        {
            McpLog.Info(
                $"[HTTP Reload] code=transport_configuration_changed " +
                $"endpoint='{GetSafeEndpoint()}' last_reason='{reason}' " +
                "retry_state=cancelled_by_transport_switch");
        }

        private static bool IsConfiguredServerReachable()
        {
            try
            {
                return HttpEndpointUtility.IsRemoteScope()
                    || MCPServiceLocator.Server.IsLocalHttpServerReachable();
            }
            catch
            {
                return false;
            }
        }

        private static string GetSafeEndpoint()
        {
            try
            {
                Uri endpoint = new Uri(HttpEndpointUtility.GetBaseUrl());
                UriBuilder safeEndpoint = new UriBuilder(endpoint)
                {
                    UserName = string.Empty,
                    Password = string.Empty
                };
                return safeEndpoint.Uri.ToString();
            }
            catch
            {
                return "unavailable";
            }
        }

        private static string GetLifecycleLogPath()
        {
            try
            {
                string baseUrl = HttpEndpointUtility.GetLocalBaseUrl();
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri) && uri.Port > 0)
                {
                    return ServerRunStateReader.GetLifecycleLogPathForPort(uri.Port);
                }
            }
            catch
            {
            }
            return "unavailable";
        }

        private static string NormalizeLogField(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "no error detail";
            }
            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\'', ' ').Trim();
        }
    }
}
