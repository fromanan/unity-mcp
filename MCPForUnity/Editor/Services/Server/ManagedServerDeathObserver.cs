using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    [InitializeOnLoad]
    internal static class ManagedServerDeathObserver
    {
        private const double PollIntervalSeconds = 5.0;
        private const int MissingPollsBeforeReport = 2;
        private static readonly TimeSpan ShutdownRequestClassificationWindow =
            TimeSpan.FromMinutes(1);
        private const string ReportedGenerationSessionKey =
            "MCPForUnity.ManagedServerDeathObserver.ReportedGeneration";
        private static readonly int CurrentUnityPid = GetCurrentUnityPid();
        private static readonly double CurrentUnityStartedAtUnix =
            GetCurrentUnityStartedAtUnix();
        private static double _nextPollAt;
        private static string _generation;
        private static int _consecutiveMissing;
        private static string _observedSupervisorGeneration;
        private static Process _observedSupervisorProcess;

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
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeObservedSupervisor;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeObservedSupervisor;
            EditorApplication.quitting -= DisposeObservedSupervisor;
            EditorApplication.quitting += DisposeObservedSupervisor;
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
            bool alreadyReported,
            bool shutdownRequested = false)
        {
            if (status == null ||
                status.SupervisorPid <= 0 ||
                status.UnityPid != currentUnityPid ||
                supervisorAlive ||
                consecutiveMissing < MissingPollsBeforeReport ||
                alreadyReported ||
                shutdownRequested ||
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

                bool supervisorAlive = ObserveExpectedProcess(
                    status,
                    generation,
                    out int? observedSupervisorExitCode);
                if (supervisorAlive || !string.IsNullOrWhiteSpace(status.ExitReason))
                {
                    _consecutiveMissing = 0;
                    if (!supervisorAlive)
                    {
                        DisposeObservedSupervisor();
                    }
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
                bool shutdownRequested = ServerRunStateReader.HasRecentShutdownRequest(
                    status,
                    DateTimeOffset.UtcNow,
                    ShutdownRequestClassificationWindow);
                if (ShouldPersistShutdownSuppression(
                        shutdownRequested,
                        _consecutiveMissing,
                        alreadyReported))
                {
                    SessionState.SetString(ReportedGenerationSessionKey, generation);
                    return;
                }
                if (!ShouldReportUnexpectedExit(
                        status,
                        CurrentUnityPid,
                        CurrentUnityStartedAtUnix,
                        supervisorAlive,
                        _consecutiveMissing,
                        alreadyReported,
                        shutdownRequested))
                {
                    return;
                }

                WriteUnexpectedExitDiagnostic(status, observedSupervisorExitCode);
                SessionState.SetString(ReportedGenerationSessionKey, generation);
                DisposeObservedSupervisor();
            }
            catch (Exception)
            {
                // Diagnostics must never interfere with the Unity Editor update loop.
            }
        }

        internal static bool ShouldPersistShutdownSuppression(
            bool shutdownRequested,
            int consecutiveMissing,
            bool alreadyReported)
        {
            return shutdownRequested
                   && consecutiveMissing >= MissingPollsBeforeReport
                   && !alreadyReported;
        }

        private static bool ObserveExpectedProcess(
            ManagedServerStatus status,
            string generation,
            out int? observedExitCode)
        {
            observedExitCode = null;
            if (status.SupervisorPid <= 0)
            {
                return false;
            }

            if (_observedSupervisorProcess == null ||
                !string.Equals(
                    _observedSupervisorGeneration,
                    generation,
                    StringComparison.Ordinal))
            {
                DisposeObservedSupervisor();
                _observedSupervisorProcess = TryOpenExpectedProcess(
                    status.SupervisorPid,
                    status.LaunchedAtUnix);
                _observedSupervisorGeneration = generation;
            }
            if (_observedSupervisorProcess == null)
            {
                return false;
            }

            try
            {
                _observedSupervisorProcess.Refresh();
                if (_observedSupervisorProcess.HasExited)
                {
                    observedExitCode = _observedSupervisorProcess.ExitCode;
                    return false;
                }

                return true;
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

        private static Process TryOpenExpectedProcess(int processId, double launchedAtUnix)
        {
            try
            {
                Process process = Process.GetProcessById(processId);
                if (launchedAtUnix <= 0)
                {
                    return process;
                }

                double processStartedAtUnix =
                    new DateTimeOffset(process.StartTime.ToUniversalTime())
                        .ToUnixTimeMilliseconds() / 1000.0;
                if (processStartedAtUnix <= launchedAtUnix + 5.0 &&
                    processStartedAtUnix >= launchedAtUnix - 60.0)
                {
                    return process;
                }

                process.Dispose();
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void DisposeObservedSupervisor()
        {
            if (_observedSupervisorProcess != null)
            {
                _observedSupervisorProcess.Dispose();
                _observedSupervisorProcess = null;
            }

            _observedSupervisorGeneration = null;
        }

        private static void WriteUnexpectedExitDiagnostic(
            ManagedServerStatus status,
            int? observedSupervisorExitCode)
        {
            ResourcePressureSnapshot pressure = CaptureResourcePressure();
            string eventName = observedSupervisorExitCode.HasValue
                ? "supervisor_exited_unclassified"
                : "supervisor_disappeared_unclassified";
            string reason = observedSupervisorExitCode.HasValue
                ? $"Supervisor exited with code {observedSupervisorExitCode.Value} before writing an exit classification"
                : "Supervisor process disappeared before writing an exit classification";
            ServerRunStateReader.TryAppendLifecycleEvent(
                status,
                eventName,
                reason,
                status.SupervisorPid,
                out string diagnosticPath,
                observedSupervisorExitCode,
                pressure.UnityPrivateBytes,
                pressure.SystemCommitUsedPercent,
                pressure.SystemAvailablePhysicalBytes,
                pressure.SystemCommitUsedBytes,
                pressure.SystemCommitLimitBytes);
            string exitCode = observedSupervisorExitCode.HasValue
                ? observedSupervisorExitCode.Value.ToString()
                : "unavailable";
            McpLog.Warn(
                $"Managed MCP supervisor PID {status.SupervisorPid} stopped without " +
                $"recording an exit reason (observed exit code: {exitCode}; " +
                $"Unity private bytes: {pressure.UnityPrivateBytes}; " +
                $"system commit: {pressure.SystemCommitUsedPercent}%; " +
                $"available physical bytes: {pressure.SystemAvailablePhysicalBytes}). " +
                $"Diagnostic: {diagnosticPath}");
        }

        private static ResourcePressureSnapshot CaptureResourcePressure()
        {
            long unityPrivateBytes = -1;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    unityPrivateBytes = process.PrivateMemorySize64;
                }
            }
            catch (Exception)
            {
            }

#if UNITY_EDITOR_WIN
            try
            {
                MemoryStatusEx memoryStatus = new MemoryStatusEx
                {
                    Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>())
                };
                if (!GlobalMemoryStatusEx(ref memoryStatus))
                {
                    return ResourcePressureSnapshot.Unavailable(unityPrivateBytes);
                }

                long commitLimitBytes = checked((long)memoryStatus.TotalPageFile);
                long commitAvailableBytes = checked((long)memoryStatus.AvailablePageFile);
                long commitUsedBytes = commitLimitBytes - commitAvailableBytes;
                int commitUsedPercent = commitLimitBytes > 0
                    ? checked((int)Math.Round(
                        commitUsedBytes * 100d / commitLimitBytes,
                        MidpointRounding.AwayFromZero))
                    : -1;
                return new ResourcePressureSnapshot(
                    unityPrivateBytes,
                    checked((long)memoryStatus.AvailablePhysical),
                    commitUsedBytes,
                    commitLimitBytes,
                    commitUsedPercent);
            }
            catch (Exception)
            {
                return ResourcePressureSnapshot.Unavailable(unityPrivateBytes);
            }
#else
            return ResourcePressureSnapshot.Unavailable(unityPrivateBytes);
#endif
        }

        private static string BuildGeneration(ManagedServerStatus status)
        {
            return ServerRunStateReader.BuildGeneration(status);
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

#if UNITY_EDITOR_WIN
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx memoryStatus);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }
#endif

        private readonly struct ResourcePressureSnapshot
        {
            public ResourcePressureSnapshot(
                long unityPrivateBytes,
                long systemAvailablePhysicalBytes,
                long systemCommitUsedBytes,
                long systemCommitLimitBytes,
                int systemCommitUsedPercent)
            {
                UnityPrivateBytes = unityPrivateBytes;
                SystemAvailablePhysicalBytes = systemAvailablePhysicalBytes;
                SystemCommitUsedBytes = systemCommitUsedBytes;
                SystemCommitLimitBytes = systemCommitLimitBytes;
                SystemCommitUsedPercent = systemCommitUsedPercent;
            }

            public long UnityPrivateBytes { get; }

            public long SystemAvailablePhysicalBytes { get; }

            public long SystemCommitUsedBytes { get; }

            public long SystemCommitLimitBytes { get; }

            public int SystemCommitUsedPercent { get; }

            public static ResourcePressureSnapshot Unavailable(long unityPrivateBytes)
            {
                return new ResourcePressureSnapshot(
                    unityPrivateBytes,
                    -1,
                    -1,
                    -1,
                    -1);
            }
        }
    }
}
