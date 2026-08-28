using System;
using MCPForUnity.Editor.Helpers;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Thread-safe, minimal shared status for Unity Test Runner execution.
    /// Used by editor readiness snapshots so callers can avoid starting overlapping runs.
    /// </summary>
    internal static class TestRunStatus
    {
        private static readonly object LockObj = new();
        internal static event Action StateChanged;

        private static bool _isRunning;
        private static TestMode? _mode;
        private static long? _startedUnixMs;
        private static long? _finishedUnixMs;
        private static string _lastResultState;
        private static int? _lastTotal;
        private static int? _lastPassed;
        private static int? _lastFailed;
        private static int? _lastSkipped;

        public static bool IsRunning
        {
            get { lock (LockObj) return _isRunning; }
        }

        public static TestMode? Mode
        {
            get { lock (LockObj) return _mode; }
        }

        public static long? StartedUnixMs
        {
            get { lock (LockObj) return _startedUnixMs; }
        }

        public static long? FinishedUnixMs
        {
            get { lock (LockObj) return _finishedUnixMs; }
        }

        public static string LastResultState
        {
            get { lock (LockObj) return _lastResultState; }
        }

        public static int? LastTotal
        {
            get { lock (LockObj) return _lastTotal; }
        }

        public static int? LastPassed
        {
            get { lock (LockObj) return _lastPassed; }
        }

        public static int? LastFailed
        {
            get { lock (LockObj) return _lastFailed; }
        }

        public static int? LastSkipped
        {
            get { lock (LockObj) return _lastSkipped; }
        }

        public static void MarkStarted(TestMode mode)
        {
            lock (LockObj)
            {
                _isRunning = true;
                _mode = mode;
                _startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _finishedUnixMs = null;
                _lastResultState = null;
                _lastTotal = null;
                _lastPassed = null;
                _lastFailed = null;
                _lastSkipped = null;
            }

            NotifyStateChanged();
        }

        public static void MarkFinished(TestRunResult result = null, string validatedOutcome = null)
        {
            lock (LockObj)
            {
                _isRunning = false;
                _finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _mode = null;
                if (result != null)
                {
                    _lastResultState = validatedOutcome ?? result.Summary.ResultState;
                    _lastTotal = result.Total;
                    _lastPassed = result.Passed;
                    _lastFailed = result.Failed;
                    _lastSkipped = result.Skipped;
                }
            }

            NotifyStateChanged();
        }

        private static void NotifyStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestRunStatus] State listener failed: {ex.Message}");
            }
        }
    }
}
