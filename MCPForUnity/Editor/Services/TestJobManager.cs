using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    internal enum TestJobStatus
    {
        Running,
        Passed,
        Failed,
        Blocked,
        InfrastructureError,
        NoTests,
        Skipped,
        Aborted,
        Cancelled
    }

    internal sealed class TestJobFailure
    {
        public string FullName { get; set; }
        public string State { get; set; }
        public string Message { get; set; }
        public string StackTrace { get; set; }
        public string Output { get; set; }
    }

    internal sealed class TestJobOptions
    {
        public bool IncludeDetails { get; set; }
        public bool IncludeFailedTests { get; set; } = true;
        public int MinimumExpectedTests { get; set; } = 1;
        public string[] ExpectedTestNames { get; set; }
        public bool FailOnSkipped { get; set; } = true;
        public TestExecutionOptions Execution { get; set; } = new();
    }

    internal sealed class TestJob
    {
        public string JobId { get; set; }
        public TestJobStatus Status { get; set; }
        public string Mode { get; set; }
        public long StartedUnixMs { get; set; }
        public long? FinishedUnixMs { get; set; }
        public long LastUpdateUnixMs { get; set; }
        public int? TotalTests { get; set; }
        public int StartedTests { get; set; }
        public int CompletedTests { get; set; }
        public string CurrentTestFullName { get; set; }
        public long? CurrentTestStartedUnixMs { get; set; }
        public string LastFinishedTestFullName { get; set; }
        public long? LastFinishedUnixMs { get; set; }
        public List<TestJobFailure> FailuresSoFar { get; set; }
        public string Error { get; set; }
        public TestRunResult Result { get; set; }
        public long InitTimeoutMs { get; set; }
        public bool IncludeDetails { get; set; }
        public bool IncludeFailedTests { get; set; }
        public int MinimumExpectedTests { get; set; }
        public List<string> ExpectedTestNames { get; set; }
        public bool FailOnSkipped { get; set; }
        public List<string> SelectedTestNames { get; set; }
        public string SelectionHash { get; set; }
        public List<string> MissingExpectedTests { get; set; }
        public string Fidelity { get; set; }
        public bool AllowSceneSave { get; set; }
        public string ArtifactDirectory { get; set; }
        public string ResultArtifactPath { get; set; }
    }

    /// <summary>
    /// Tracks async test jobs started via MCP tools. This is not intended to capture manual Test Runner UI runs.
    /// </summary>
    internal static class TestJobManager
    {
        // Keep this small to avoid ballooning payloads during polling.
        private const int FailureCap = 25;
        private const long StuckThresholdMs = 60_000;
        private const long DefaultInitializationTimeoutMs = 15_000; // 15 seconds default; override per-job via run_tests init_timeout param
        private const long MaxInitializationTimeoutMs = 600_000; // 10 minutes hard cap
        private const int MaxJobsToKeep = 10;
        private const long MinPersistIntervalMs = 1000; // Throttle persistence to reduce overhead

        // SessionState survives domain reloads within the same Unity Editor session.
        private const string SessionKeyJobs = "MCPForUnity.TestJobsV1";
        private const string SessionKeyCurrentJobId = "MCPForUnity.CurrentTestJobIdV1";

        private static readonly object LockObj = new();
        private static readonly Dictionary<string, TestJob> Jobs = new();
        private static string _currentJobId;
        private static long _lastPersistUnixMs;

        static TestJobManager()
        {
            // Restore after domain reloads (e.g., compilation while a job is running).
            TryRestoreFromSessionState();
            TryRestoreRecentArtifacts();
        }

        public static string CurrentJobId
        {
            get { lock (LockObj) return _currentJobId; }
        }

        public static bool HasRunningJob
        {
            get
            {
                lock (LockObj)
                {
                    return !string.IsNullOrEmpty(_currentJobId);
                }
            }
        }

        /// <summary>
        /// Force-clears any stuck or orphaned test job. Call this when tests get stuck due to
        /// assembly reloads or other interruptions.
        /// </summary>
        /// <returns>True if a job was cleared, false if no running job exists.</returns>
        public static bool ClearStuckJob()
        {
            bool cleared = false;
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId))
                {
                    return false;
                }

                if (Jobs.TryGetValue(_currentJobId, out var job) && job.Status == TestJobStatus.Running)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    job.Status = TestJobStatus.Aborted;
                    job.Error = "Job cleared manually (stuck or orphaned)";
                    job.FinishedUnixMs = now;
                    job.LastUpdateUnixMs = now;
                    McpLog.Warn($"[TestJobManager] Manually cleared stuck job {_currentJobId}");
                    cleared = true;
                }

                _currentJobId = null;
            }
            PersistToSessionState(force: true);
            return cleared;
        }

        private sealed class PersistedState
        {
            public string current_job_id { get; set; }
            public List<PersistedJob> jobs { get; set; }
        }

        private sealed class PersistedJob
        {
            public string job_id { get; set; }
            public string status { get; set; }
            public string mode { get; set; }
            public long started_unix_ms { get; set; }
            public long? finished_unix_ms { get; set; }
            public long last_update_unix_ms { get; set; }
            public int? total_tests { get; set; }
            public int started_tests { get; set; }
            public int completed_tests { get; set; }
            public string current_test_full_name { get; set; }
            public long? current_test_started_unix_ms { get; set; }
            public string last_finished_test_full_name { get; set; }
            public long? last_finished_unix_ms { get; set; }
            public List<TestJobFailure> failures_so_far { get; set; }
            public string error { get; set; }
            public long init_timeout_ms { get; set; }
            public bool include_details { get; set; }
            public bool include_failed_tests { get; set; }
            public int minimum_expected_tests { get; set; }
            public List<string> expected_test_names { get; set; }
            public bool? fail_on_skipped { get; set; }
            public List<string> selected_test_names { get; set; }
            public string selection_hash { get; set; }
            public List<string> missing_expected_tests { get; set; }
            public string fidelity { get; set; }
            public bool allow_scene_save { get; set; }
            public string artifact_directory { get; set; }
            public string result_artifact_path { get; set; }
        }

        private static TestJobStatus ParseStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return TestJobStatus.Running;
            }

            string s = status.Trim().ToLowerInvariant();
            return s switch
            {
                "passed" => TestJobStatus.Passed,
                "succeeded" => TestJobStatus.Passed,
                "failed" => TestJobStatus.Failed,
                "blocked" => TestJobStatus.Blocked,
                "infrastructureerror" => TestJobStatus.InfrastructureError,
                "infrastructure_error" => TestJobStatus.InfrastructureError,
                "notests" => TestJobStatus.NoTests,
                "no_tests" => TestJobStatus.NoTests,
                "skipped" => TestJobStatus.Skipped,
                "aborted" => TestJobStatus.Aborted,
                "cancelled" => TestJobStatus.Cancelled,
                "canceled" => TestJobStatus.Cancelled,
                _ => TestJobStatus.Running
            };
        }

        private static void TryRestoreFromSessionState()
        {
            try
            {
                string json = SessionState.GetString(SessionKeyJobs, string.Empty);
                if (string.IsNullOrWhiteSpace(json))
                {
                    var legacy = SessionState.GetString(SessionKeyCurrentJobId, string.Empty);
                    _currentJobId = string.IsNullOrWhiteSpace(legacy) ? null : legacy;
                    return;
                }

                var state = JsonConvert.DeserializeObject<PersistedState>(json);
                if (state?.jobs == null)
                {
                    return;
                }

                lock (LockObj)
                {
                    Jobs.Clear();
                    foreach (var pj in state.jobs)
                    {
                        if (pj == null || string.IsNullOrWhiteSpace(pj.job_id))
                        {
                            continue;
                        }

                        Jobs[pj.job_id] = new TestJob
                        {
                            JobId = pj.job_id,
                            Status = ParseStatus(pj.status),
                            Mode = pj.mode,
                            StartedUnixMs = pj.started_unix_ms,
                            FinishedUnixMs = pj.finished_unix_ms,
                            LastUpdateUnixMs = pj.last_update_unix_ms,
                            TotalTests = pj.total_tests,
                            StartedTests = pj.started_tests,
                            CompletedTests = pj.completed_tests,
                            CurrentTestFullName = pj.current_test_full_name,
                            CurrentTestStartedUnixMs = pj.current_test_started_unix_ms,
                            LastFinishedTestFullName = pj.last_finished_test_full_name,
                            LastFinishedUnixMs = pj.last_finished_unix_ms,
                            FailuresSoFar = pj.failures_so_far ?? new List<TestJobFailure>(),
                            Error = pj.error,
                            InitTimeoutMs = pj.init_timeout_ms,
                            IncludeDetails = pj.include_details,
                            IncludeFailedTests = pj.include_failed_tests,
                            MinimumExpectedTests = pj.minimum_expected_tests > 0 ? pj.minimum_expected_tests : 1,
                            ExpectedTestNames = pj.expected_test_names ?? new List<string>(),
                            FailOnSkipped = pj.fail_on_skipped ?? true,
                            SelectedTestNames = pj.selected_test_names ?? new List<string>(),
                            SelectionHash = pj.selection_hash,
                            MissingExpectedTests = pj.missing_expected_tests ?? new List<string>(),
                            Fidelity = pj.fidelity ?? TestExecutionFidelity.Native.ToString(),
                            AllowSceneSave = pj.allow_scene_save,
                            ArtifactDirectory = pj.artifact_directory,
                            ResultArtifactPath = pj.result_artifact_path,
                            // Intentionally not persisted to avoid ballooning SessionState.
                            Result = null
                        };
                    }

                    _currentJobId = string.IsNullOrWhiteSpace(state.current_job_id) ? null : state.current_job_id;
                    if (!string.IsNullOrEmpty(_currentJobId) && !Jobs.ContainsKey(_currentJobId))
                    {
                        _currentJobId = null;
                    }

                    // Detect and clean up stale "running" jobs that were orphaned by domain reload.
                    // After a domain reload, TestRunStatus resets to not-running, but _currentJobId
                    // may still be set. If the job hasn't been updated recently, it's likely orphaned.
                    if (!string.IsNullOrEmpty(_currentJobId) && Jobs.TryGetValue(_currentJobId, out var currentJob))
                    {
                        if (currentJob.Status == TestJobStatus.Running)
                        {
                            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            long staleCutoffMs = 5 * 60 * 1000; // 5 minutes
                            if (now - currentJob.LastUpdateUnixMs > staleCutoffMs)
                            {
                                McpLog.Warn($"[TestJobManager] Clearing stale job {_currentJobId} (last update {(now - currentJob.LastUpdateUnixMs) / 1000}s ago)");
                                currentJob.Status = TestJobStatus.Aborted;
                                currentJob.Error = "Job orphaned after domain reload";
                                currentJob.FinishedUnixMs = now;
                                _currentJobId = null;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Restoration is best-effort; never block editor load.
                McpLog.Warn($"[TestJobManager] Failed to restore SessionState: {ex.Message}");
            }
        }

        private static void PersistToSessionState(bool force = false)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            // Throttle non-critical updates to reduce overhead during large test runs
            if (!force && (now - _lastPersistUnixMs) < MinPersistIntervalMs)
            {
                return;
            }
            
            try
            {
                PersistedState snapshot;
                lock (LockObj)
                {
                    var jobs = Jobs.Values
                        .OrderByDescending(j => j.LastUpdateUnixMs)
                        .Take(MaxJobsToKeep)
                        .Select(j => new PersistedJob
                        {
                            job_id = j.JobId,
                            status = j.Status.ToString().ToLowerInvariant(),
                            mode = j.Mode,
                            started_unix_ms = j.StartedUnixMs,
                            finished_unix_ms = j.FinishedUnixMs,
                            last_update_unix_ms = j.LastUpdateUnixMs,
                            total_tests = j.TotalTests,
                            started_tests = j.StartedTests,
                            completed_tests = j.CompletedTests,
                            current_test_full_name = j.CurrentTestFullName,
                            current_test_started_unix_ms = j.CurrentTestStartedUnixMs,
                            last_finished_test_full_name = j.LastFinishedTestFullName,
                            last_finished_unix_ms = j.LastFinishedUnixMs,
                            failures_so_far = (j.FailuresSoFar ?? new List<TestJobFailure>()).Take(FailureCap).ToList(),
                            error = j.Error,
                            init_timeout_ms = j.InitTimeoutMs,
                            include_details = j.IncludeDetails,
                            include_failed_tests = j.IncludeFailedTests,
                            minimum_expected_tests = j.MinimumExpectedTests,
                            expected_test_names = j.ExpectedTestNames,
                            fail_on_skipped = j.FailOnSkipped,
                            selected_test_names = j.SelectedTestNames,
                            selection_hash = j.SelectionHash,
                            missing_expected_tests = j.MissingExpectedTests,
                            fidelity = j.Fidelity,
                            allow_scene_save = j.AllowSceneSave,
                            artifact_directory = j.ArtifactDirectory,
                            result_artifact_path = j.ResultArtifactPath
                        })
                        .ToList();

                    snapshot = new PersistedState
                    {
                        current_job_id = _currentJobId,
                        jobs = jobs
                    };
                }

                SessionState.SetString(SessionKeyCurrentJobId, snapshot.current_job_id ?? string.Empty);
                SessionState.SetString(SessionKeyJobs, JsonConvert.SerializeObject(snapshot));
                _lastPersistUnixMs = now;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestJobManager] Failed to persist SessionState: {ex.Message}");
            }
        }

        public static string StartJob(
            TestMode mode,
            TestFilterOptions filterOptions = null,
            long initTimeoutMs = 0,
            TestJobOptions options = null)
        {
            options ??= new TestJobOptions();
            options.Execution ??= new TestExecutionOptions();
            // Clamp to valid range: non-positive values mean "use default", cap at 10 minutes
            if (initTimeoutMs < 0) initTimeoutMs = 0;
            if (initTimeoutMs > MaxInitializationTimeoutMs) initTimeoutMs = MaxInitializationTimeoutMs;

            string jobId = Guid.NewGuid().ToString("N");
            long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string modeStr = mode.ToString();

            TestJob job = new()
            {
                JobId = jobId,
                Status = TestJobStatus.Running,
                Mode = modeStr,
                StartedUnixMs = started,
                FinishedUnixMs = null,
                LastUpdateUnixMs = started,
                TotalTests = null,
                StartedTests = 0,
                CompletedTests = 0,
                CurrentTestFullName = null,
                CurrentTestStartedUnixMs = null,
                LastFinishedTestFullName = null,
                LastFinishedUnixMs = null,
                FailuresSoFar = new List<TestJobFailure>(),
                Error = null,
                Result = null,
                InitTimeoutMs = initTimeoutMs,
                IncludeDetails = options.IncludeDetails,
                IncludeFailedTests = options.IncludeFailedTests,
                MinimumExpectedTests = Math.Max(1, options.MinimumExpectedTests),
                ExpectedTestNames = NormalizeTestNames(options.ExpectedTestNames),
                FailOnSkipped = options.FailOnSkipped,
                SelectedTestNames = new List<string>(),
                SelectionHash = null,
                MissingExpectedTests = new List<string>(),
                Fidelity = options.Execution.Fidelity.ToString(),
                AllowSceneSave = options.Execution.AllowSceneSave,
                ArtifactDirectory = GetArtifactDirectory(jobId),
                ResultArtifactPath = null
            };

            // Single lock scope for check-and-set to avoid TOCTOU race
            lock (LockObj)
            {
                if (!string.IsNullOrEmpty(_currentJobId))
                {
                    throw new InvalidOperationException("A Unity test run is already in progress.");
                }
                Jobs[jobId] = job;
                _currentJobId = jobId;
            }
            PersistToSessionState(force: true);
            PersistJobArtifacts(job, "started");

            // Kick the run (must be called on main thread; our command handlers already run there).
            Task<TestRunResult> task = MCPServiceLocator.Tests.RunTestsAsync(
                mode,
                filterOptions,
                options.Execution);

            void FinalizeJob(Action finalize)
            {
                // Ensure state mutation happens on main thread to avoid Unity API surprises.
                EditorApplication.delayCall += () =>
                {
                    try { finalize(); }
                    catch (Exception ex) { McpLog.Error($"[TestJobManager] Finalize failed: {ex.Message}\n{ex.StackTrace}"); }
                };
            }

            task.ContinueWith(t =>
            {
                // NOTE: We now finalize jobs deterministically from the TestRunnerService RunFinished callback.
                // This continuation is retained as a safety net in case RunFinished is not delivered.
                FinalizeJob(() => FinalizeFromTask(jobId, t));
            }, TaskScheduler.Default);

            return jobId;
        }

        public static TestJobStatus? FinalizeCurrentJobFromRunFinished(TestRunResult resultPayload)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TestJob completedJob = null;
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return null;
                }

                job.LastUpdateUnixMs = now;
                job.FinishedUnixMs = now;
                job.Result = resultPayload;
                job.Status = EvaluateOutcome(
                    job,
                    resultPayload,
                    out string outcomeError,
                    out List<string> missingExpectedTests);
                job.Error = outcomeError;
                job.MissingExpectedTests = missingExpectedTests;
                job.CurrentTestFullName = null;
                _currentJobId = null;
                completedJob = job;
            }
            PersistToSessionState(force: true);
            PersistJobArtifacts(completedJob, "finished");
            return completedJob?.Status;
        }

        public static void OnRunStarted(IReadOnlyList<string> selectedTestNames)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TestJob runningJob = null;
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.SelectedTestNames = NormalizeTestNames(selectedTestNames);
                job.TotalTests = selectedTestNames == null ? null : job.SelectedTestNames.Count;
                job.SelectionHash = ComputeSelectionHash(job.SelectedTestNames);
                job.StartedTests = 0;
                job.CompletedTests = 0;
                job.CurrentTestFullName = null;
                job.CurrentTestStartedUnixMs = null;
                job.LastFinishedTestFullName = null;
                job.LastFinishedUnixMs = null;
                job.FailuresSoFar ??= new List<TestJobFailure>();
                job.FailuresSoFar.Clear();
                runningJob = job;
            }
            PersistToSessionState(force: true);
            PersistJobArtifacts(runningJob, "run_started");
        }

        public static void OnTestStarted(string testFullName, bool isLeaf)
        {
            if (string.IsNullOrWhiteSpace(testFullName))
            {
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.CurrentTestFullName = testFullName;
                job.CurrentTestStartedUnixMs = now;
                if (isLeaf)
                {
                    job.StartedTests = Math.Max(0, job.StartedTests + 1);
                }
            }
            PersistToSessionState();
        }

        public static void OnLeafTestFinished(
            string testFullName,
            string state,
            bool isFailure,
            string message,
            string stackTrace,
            string output)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.CompletedTests = Math.Max(0, job.CompletedTests + 1);
                job.LastFinishedTestFullName = testFullName;
                job.LastFinishedUnixMs = now;

                if (isFailure)
                {
                    job.FailuresSoFar ??= new List<TestJobFailure>();
                    if (job.FailuresSoFar.Count < FailureCap)
                    {
                        job.FailuresSoFar.Add(new TestJobFailure
                        {
                            FullName = testFullName,
                            State = state,
                            Message = string.IsNullOrWhiteSpace(message) ? "Test failed" : message,
                            StackTrace = stackTrace,
                            Output = output
                        });
                    }
                }
            }
            PersistToSessionState();
        }

        public static void OnRunFinished()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId) || !Jobs.TryGetValue(_currentJobId, out var job))
                {
                    return;
                }

                job.LastUpdateUnixMs = now;
                job.CurrentTestFullName = null;
            }
            PersistToSessionState(force: true);
        }

        internal static TestJob GetJob(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            TestJob jobToReturn = null;
            bool shouldPersist = false;
            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out var job))
                {
                    return null;
                }

                // Check if job is stuck in "running" state without having called OnRunStarted (TotalTests still null).
                // This happens when tests fail to initialize (e.g., unsaved scene, compilation issues).
                // After 15 seconds without initialization, auto-fail the job to prevent hanging.
                if (job.Status == TestJobStatus.Running && job.TotalTests == null)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long initTimeout = job.InitTimeoutMs > 0 ? job.InitTimeoutMs : DefaultInitializationTimeoutMs;
                    if (!EditorStateCache.GetActualIsCompiling() && !EditorApplication.isUpdating && now - job.StartedUnixMs > initTimeout)
                    {
                        McpLog.Warn($"[TestJobManager] Job {jobId} failed to initialize within {initTimeout}ms, auto-failing");
                        job.Status = TestJobStatus.InfrastructureError;
                        job.Error = "Test job failed to initialize (tests did not start within timeout)";
                        job.FinishedUnixMs = now;
                        job.LastUpdateUnixMs = now;
                        if (_currentJobId == jobId)
                        {
                            _currentJobId = null;
                            // Keep TestRunStatus in sync: when initialization times out, neither
                            // RunStarted nor RunFinished fires, so the running flag would otherwise leak.
                            // Only clear it if this job is still the active one — a newer job may have taken over.
                            TestRunStatus.MarkFinished();
                        }
                        shouldPersist = true;
                    }
                }

                jobToReturn = job;
            }

            if (shouldPersist)
            {
                PersistToSessionState(force: true);
            }
            return jobToReturn;
        }

        internal static object ToSerializable(TestJob job, bool includeDetails, bool includeFailedTests)
        {
            if (job == null)
            {
                return null;
            }

            bool effectiveIncludeDetails = includeDetails || job.IncludeDetails;
            bool effectiveIncludeFailedTests = includeFailedTests || job.IncludeFailedTests;
            object resultPayload = null;
            if (job.Result != null)
            {
                resultPayload = job.Result.ToSerializable(
                    job.Mode,
                    effectiveIncludeDetails,
                    effectiveIncludeFailedTests);
            }
            else if (!string.IsNullOrWhiteSpace(job.ResultArtifactPath))
            {
                resultPayload = TryReadResultArtifact(job.ResultArtifactPath);
            }

            string outcome = ToOutcomeString(job.Status);

            return new
            {
                job_id = job.JobId,
                status = outcome,
                outcome,
                validation_passed = job.Status == TestJobStatus.Passed,
                transport_success = true,
                mode = job.Mode,
                fidelity = job.Fidelity,
                allow_scene_save = job.AllowSceneSave,
                started_unix_ms = job.StartedUnixMs,
                finished_unix_ms = job.FinishedUnixMs,
                last_update_unix_ms = job.LastUpdateUnixMs,
                progress = new
                {
                    selected = job.TotalTests,
                    started = job.StartedTests,
                    completed = job.CompletedTests,
                    total = job.TotalTests,
                    current_test_full_name = job.CurrentTestFullName,
                    current_test_started_unix_ms = job.CurrentTestStartedUnixMs,
                    last_finished_test_full_name = job.LastFinishedTestFullName,
                    last_finished_unix_ms = job.LastFinishedUnixMs,
                    stuck_suspected = IsStuck(job),
                    editor_is_focused = InternalEditorUtility.isApplicationActive,
                    blocked_reason = GetBlockedReason(job),
                    failures_so_far = BuildFailuresPayload(job.FailuresSoFar),
                    failures_capped = (job.FailuresSoFar != null && job.FailuresSoFar.Count >= FailureCap)
                },
                coverage = new
                {
                    minimum_expected_tests = job.MinimumExpectedTests,
                    expected_test_names = job.ExpectedTestNames,
                    selected_test_names = job.SelectedTestNames,
                    selection_hash = job.SelectionHash,
                    missing_expected_tests = job.MissingExpectedTests,
                    fail_on_skipped = job.FailOnSkipped
                },
                error = job.Error,
                result = resultPayload,
                artifacts = new
                {
                    directory = job.ArtifactDirectory,
                    result = job.ResultArtifactPath
                }
            };
        }

        private static string GetBlockedReason(TestJob job)
        {
            if (job == null || job.Status != TestJobStatus.Running)
            {
                return null;
            }

            if (!IsStuck(job))
            {
                return null;
            }

            // This matches the real-world symptom you observed: background Unity can get heavily throttled by OS/Editor.
            if (!InternalEditorUtility.isApplicationActive)
            {
                return "editor_unfocused";
            }

            if (EditorStateCache.GetActualIsCompiling())
            {
                return "compiling";
            }

            if (EditorApplication.isUpdating)
            {
                return "asset_import";
            }

            return "unknown";
        }

        private static bool IsStuck(TestJob job)
        {
            if (job == null || job.Status != TestJobStatus.Running)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(job.CurrentTestFullName) || !job.CurrentTestStartedUnixMs.HasValue)
            {
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return (now - job.CurrentTestStartedUnixMs.Value) > StuckThresholdMs;
        }

        private static object[] BuildFailuresPayload(List<TestJobFailure> failures)
        {
            if (failures == null || failures.Count == 0)
            {
                return Array.Empty<object>();
            }

            var list = new object[failures.Count];
            for (int i = 0; i < failures.Count; i++)
            {
                var f = failures[i];
                list[i] = new
                {
                    full_name = f?.FullName,
                    state = f?.State,
                    message = f?.Message,
                    stack_trace = f?.StackTrace,
                    output = f?.Output
                };
            }
            return list;
        }

        internal static TestJobStatus EvaluateOutcome(
            TestJob job,
            TestRunResult result,
            out string error,
            out List<string> missingExpectedTests)
        {
            error = null;
            missingExpectedTests = new List<string>();
            if (result == null)
            {
                error = "Unity returned no test result payload.";
                return TestJobStatus.InfrastructureError;
            }

            List<string> selectedTestNames = job?.SelectedTestNames ?? new List<string>();
            HashSet<string> selected = new(selectedTestNames, StringComparer.Ordinal);
            List<string> expected = job?.ExpectedTestNames ?? new List<string>();
            missingExpectedTests = expected
                .Where(testName => !selected.Contains(testName))
                .OrderBy(testName => testName, StringComparer.Ordinal)
                .ToList();
            if (missingExpectedTests.Count > 0)
            {
                error = $"Expected tests were not selected: {string.Join(", ", missingExpectedTests)}";
                return TestJobStatus.Blocked;
            }

            int selectedCount = job?.TotalTests ?? result.Total;
            int minimumExpectedTests = Math.Max(1, job?.MinimumExpectedTests ?? 1);
            if (selectedCount <= 0 || result.Total <= 0 || result.Passed + result.Failed + result.Skipped <= 0)
            {
                error = "No tests were selected or executed.";
                return TestJobStatus.NoTests;
            }

            if (selectedCount < minimumExpectedTests || result.Total < minimumExpectedTests)
            {
                error = $"Expected at least {minimumExpectedTests} tests, but selected {selectedCount} and reported {result.Total}.";
                return TestJobStatus.NoTests;
            }

            string resultState = result.Summary.ResultState?.Trim() ?? string.Empty;
            string normalizedState = resultState.ToLowerInvariant();
            if (result.Failed > 0 || normalizedState.Contains("failed") || normalizedState.Contains("error"))
            {
                error = $"{result.Failed} test(s) failed.";
                return TestJobStatus.Failed;
            }

            if (normalizedState.Contains("cancel"))
            {
                error = "Unity cancelled the test run.";
                return TestJobStatus.Cancelled;
            }

            if (normalizedState.Contains("abort") || normalizedState.Contains("not runnable"))
            {
                error = $"Unity ended the test run with state '{resultState}'.";
                return TestJobStatus.Aborted;
            }

            if (result.Skipped > 0 && (job?.FailOnSkipped ?? true))
            {
                error = $"{result.Skipped} test(s) were skipped or inconclusive.";
                return TestJobStatus.Skipped;
            }

            if (normalizedState.Contains("skip") || normalizedState.Contains("inconclusive"))
            {
                error = $"Unity ended the test run with state '{resultState}'.";
                return TestJobStatus.Skipped;
            }

            int completedCount = job?.CompletedTests ?? result.Total;
            if (completedCount != selectedCount || result.Total != selectedCount)
            {
                error = $"Test execution was incomplete: selected={selectedCount}, completed={completedCount}, reported={result.Total}.";
                return TestJobStatus.Aborted;
            }

            if (!string.Equals(resultState, "Passed", StringComparison.OrdinalIgnoreCase))
            {
                error = $"Unity returned non-passing result state '{resultState}'.";
                return TestJobStatus.InfrastructureError;
            }

            return TestJobStatus.Passed;
        }

        private static List<string> NormalizeTestNames(IEnumerable<string> names)
        {
            if (names == null)
            {
                return new List<string>();
            }

            return names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        private static string ComputeSelectionHash(IReadOnlyList<string> selectedTestNames)
        {
            if (selectedTestNames == null)
            {
                return null;
            }

            string manifest = string.Join("\n", selectedTestNames);
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(manifest));
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        internal static string ToOutcomeString(TestJobStatus status)
        {
            return status switch
            {
                TestJobStatus.InfrastructureError => "infrastructure_error",
                TestJobStatus.NoTests => "no_tests",
                _ => status.ToString().ToLowerInvariant()
            };
        }

        private static string GetArtifactDirectory(string jobId)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projectRoot, "Library", "MCPForUnity", "ValidationRuns", jobId);
        }

        private static void PersistJobArtifacts(TestJob job, string eventName)
        {
            if (job == null || string.IsNullOrWhiteSpace(job.ArtifactDirectory))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(job.ArtifactDirectory);
                if (job.Result != null)
                {
                    string resultPath = Path.Combine(job.ArtifactDirectory, "results.json");
                    string resultJson = JsonConvert.SerializeObject(
                        job.Result.ToSerializable(job.Mode, includeDetails: true, includeFailedTests: true),
                        Formatting.Indented);
                    WriteAtomic(resultPath, resultJson);
                    job.ResultArtifactPath = resultPath;
                }

                string runPath = Path.Combine(job.ArtifactDirectory, "run.json");
                string runJson = JsonConvert.SerializeObject(ToSerializable(job, true, true), Formatting.Indented);
                WriteAtomic(runPath, runJson);

                string timelinePath = Path.Combine(job.ArtifactDirectory, "timeline.jsonl");
                string timelineEntry = JsonConvert.SerializeObject(new
                {
                    timestamp_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    @event = eventName,
                    outcome = ToOutcomeString(job.Status),
                    selected = job.TotalTests,
                    started = job.StartedTests,
                    completed = job.CompletedTests,
                    current_test = job.CurrentTestFullName
                });
                File.AppendAllText(timelinePath, timelineEntry + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestJobManager] Failed to persist artifacts for {job.JobId}: {ex.Message}");
            }
        }

        private static void WriteAtomic(string path, string contents)
        {
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temporaryPath, path);
        }

        private static object TryReadResultArtifact(string path)
        {
            try
            {
                return File.Exists(path) ? JToken.Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestJobManager] Failed to read result artifact '{path}': {ex.Message}");
                return null;
            }
        }

        private static void TryRestoreRecentArtifacts()
        {
            try
            {
                string root = Path.Combine(
                    Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                    "Library",
                    "MCPForUnity",
                    "ValidationRuns");
                if (!Directory.Exists(root))
                {
                    return;
                }

                IEnumerable<string> runFiles = Directory
                    .EnumerateFiles(root, "run.json", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(MaxJobsToKeep);
                lock (LockObj)
                {
                    foreach (string runFile in runFiles)
                    {
                        JObject payload = JObject.Parse(File.ReadAllText(runFile));
                        string jobId = payload["job_id"]?.ToString();
                        if (string.IsNullOrWhiteSpace(jobId) || Jobs.ContainsKey(jobId))
                        {
                            continue;
                        }

                        TestJobStatus restoredStatus = ParseStatus(payload["status"]?.ToString());
                        string restoredError = payload["error"]?.ToString();
                        if (restoredStatus == TestJobStatus.Running)
                        {
                            restoredStatus = TestJobStatus.Aborted;
                            restoredError = "Test job was interrupted by an Editor or server restart.";
                        }

                        JObject progress = payload["progress"] as JObject;
                        JObject coverage = payload["coverage"] as JObject;
                        JObject artifacts = payload["artifacts"] as JObject;
                        Jobs[jobId] = new TestJob
                        {
                            JobId = jobId,
                            Status = restoredStatus,
                            Mode = payload["mode"]?.ToString(),
                            StartedUnixMs = payload["started_unix_ms"]?.Value<long>() ?? 0,
                            FinishedUnixMs = payload["finished_unix_ms"]?.Value<long?>(),
                            LastUpdateUnixMs = payload["last_update_unix_ms"]?.Value<long>() ?? 0,
                            TotalTests = progress?["total"]?.Value<int?>(),
                            StartedTests = progress?["started"]?.Value<int>() ?? 0,
                            CompletedTests = progress?["completed"]?.Value<int>() ?? 0,
                            FailuresSoFar = new List<TestJobFailure>(),
                            Error = restoredError,
                            IncludeFailedTests = true,
                            MinimumExpectedTests = coverage?["minimum_expected_tests"]?.Value<int>() ?? 1,
                            ExpectedTestNames = coverage?["expected_test_names"]?.ToObject<List<string>>() ?? new List<string>(),
                            FailOnSkipped = coverage?["fail_on_skipped"]?.Value<bool>() ?? true,
                            SelectedTestNames = coverage?["selected_test_names"]?.ToObject<List<string>>() ?? new List<string>(),
                            SelectionHash = coverage?["selection_hash"]?.ToString(),
                            MissingExpectedTests = coverage?["missing_expected_tests"]?.ToObject<List<string>>() ?? new List<string>(),
                            Fidelity = payload["fidelity"]?.ToString() ?? TestExecutionFidelity.Native.ToString(),
                            AllowSceneSave = payload["allow_scene_save"]?.Value<bool>() ?? false,
                            ArtifactDirectory = artifacts?["directory"]?.ToString() ?? Path.GetDirectoryName(runFile),
                            ResultArtifactPath = artifacts?["result"]?.ToString()
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestJobManager] Failed to restore durable test artifacts: {ex.Message}");
            }
        }

        private static void FinalizeFromTask(string jobId, Task<TestRunResult> task)
        {
            TestJob completedJob = null;
            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out var existing))
                {
                    if (_currentJobId == jobId) _currentJobId = null;
                    return;
                }

                // If RunFinished already finalized the job, do nothing.
                if (existing.Status != TestJobStatus.Running)
                {
                    if (_currentJobId == jobId) _currentJobId = null;
                    return;
                }

                existing.LastUpdateUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                existing.FinishedUnixMs = existing.LastUpdateUnixMs;

                if (task.IsFaulted)
                {
                    Exception exception = task.Exception?.GetBaseException();
                    existing.Status = exception is TestRunBlockedException
                        ? TestJobStatus.Blocked
                        : TestJobStatus.InfrastructureError;
                    existing.Error = exception?.Message ?? "Unknown test infrastructure failure";
                    existing.Result = null;
                }
                else if (task.IsCanceled)
                {
                    existing.Status = TestJobStatus.Cancelled;
                    existing.Error = "Test job canceled";
                    existing.Result = null;
                }
                else
                {
                    TestRunResult result = task.Result;
                    existing.Result = result;
                    existing.Status = EvaluateOutcome(
                        existing,
                        result,
                        out string outcomeError,
                        out List<string> missingExpectedTests);
                    existing.Error = outcomeError;
                    existing.MissingExpectedTests = missingExpectedTests;
                }

                if (_currentJobId == jobId)
                {
                    _currentJobId = null;
                }
                completedJob = existing;
            }
            PersistToSessionState(force: true);
            PersistJobArtifacts(completedJob, "finished");
        }
    }
}
