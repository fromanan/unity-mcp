using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Restores <see cref="EditorSettings.enterPlayModeOptionsEnabled"/> and
    /// <see cref="EditorSettings.enterPlayModeOptions"/> if a previous test run was interrupted
    /// (e.g. by domain reload or editor crash) before <see cref="TestRunnerService"/> could restore them.
    /// Two persistence layers:
    /// <list type="bullet">
    /// <item><see cref="SessionState"/> — survives domain reloads within the same editor session.</item>
    /// <item>A marker file in <c>Library/</c> — survives editor crashes and force quits.</item>
    /// </list>
    /// </summary>
    [InitializeOnLoad]
    internal static class PlayModeOptionsGuard
    {
        private const string KeyPending = "MCPForUnity.PlayModeOptions.PendingRestore";
        private const string KeyEnabled = "MCPForUnity.PlayModeOptions.OriginalEnabled";
        private const string KeyOptions = "MCPForUnity.PlayModeOptions.OriginalOptions";

        // Library/ is project-local and gitignored by default.
        private static readonly string MarkerPath = Path.Combine("Library", "MCPPlayModeOptionsBackup.txt");

        static PlayModeOptionsGuard()
        {
            // After domain reload or editor restart: if a restore is pending and no test run
            // is active, restore now. TryLoad checks SessionState first, then the marker file.
            if (TryLoad(out _, out _) && !TestRunStatus.IsRunning && !TestJobManager.HasRunningJob)
            {
                Restore();
            }
        }

        internal static bool IsPending => TryLoad(out _, out _);

        internal static void Save(bool originalEnabled, EnterPlayModeOptions originalOptions)
        {
            // SessionState (domain reload)
            SessionState.SetBool(KeyEnabled, originalEnabled);
            SessionState.SetInt(KeyOptions, (int)originalOptions);
            SessionState.SetBool(KeyPending, true);

            // Marker file (crash recovery). Two lines: enabled flag, then options int.
            try
            {
                File.WriteAllText(MarkerPath, $"{(originalEnabled ? 1 : 0)}\n{(int)originalOptions}");
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[PlayModeOptionsGuard] Failed to write marker file: {ex.Message}");
            }
        }

        internal static void Restore()
        {
            if (!TryLoad(out bool origEnabled, out EnterPlayModeOptions origOptions))
            {
                return;
            }

            EditorSettings.enterPlayModeOptions = origOptions;
            EditorSettings.enterPlayModeOptionsEnabled = origEnabled;
            Clear();
            McpLog.Info("[PlayModeOptionsGuard] Restored enterPlayModeOptions after interrupted test run.");
        }

        internal static void Clear()
        {
            SessionState.SetBool(KeyPending, false);
            try
            {
                if (File.Exists(MarkerPath))
                {
                    File.Delete(MarkerPath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        /// <summary>
        /// Checks SessionState first (available after domain reload), then falls back to the
        /// marker file (available after editor crash/restart).
        /// </summary>
        private static bool TryLoad(out bool originalEnabled, out EnterPlayModeOptions originalOptions)
        {
            // Fast path: SessionState is available after domain reload.
            if (SessionState.GetBool(KeyPending, false))
            {
                originalEnabled = SessionState.GetBool(KeyEnabled, false);
                originalOptions = (EnterPlayModeOptions)SessionState.GetInt(KeyOptions, 0);
                return true;
            }

            // Slow path: marker file survives editor crash/restart.
            originalEnabled = false;
            originalOptions = EnterPlayModeOptions.None;
            try
            {
                if (!File.Exists(MarkerPath))
                {
                    return false;
                }

                string[] lines = File.ReadAllLines(MarkerPath);
                if (lines.Length < 2)
                {
                    return false;
                }

                if (!int.TryParse(lines[0].Trim(), out int enabledInt) ||
                    !int.TryParse(lines[1].Trim(), out int optionsInt))
                {
                    return false;
                }

                originalEnabled = enabledInt != 0;
                originalOptions = (EnterPlayModeOptions)optionsInt;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Concrete implementation of <see cref="ITestRunnerService"/>.
    /// Coordinates Unity Test Runner operations and produces structured results.
    /// </summary>
    internal sealed class TestRunnerService : ITestRunnerService, ICallbacks, IDisposable
    {
        private static readonly TestMode[] AllModes = { TestMode.EditMode, TestMode.PlayMode };
        private static int _liveCallbackOwnerCount;

        private readonly TestRunnerApi _testRunnerApi;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private readonly List<ITestResultAdaptor> _leafResults = new List<ITestResultAdaptor>();
        private TaskCompletionSource<TestRunResult> _runCompletionSource;
        private bool _callbacksRegistered;
        private string _activeMcpJobId;

        internal static bool HasLiveCallbackOwner => _liveCallbackOwnerCount > 0;

        public TestRunnerService()
        {
            _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            _testRunnerApi.RegisterCallbacks(this);
            _callbacksRegistered = true;
            Interlocked.Increment(ref _liveCallbackOwnerCount);
        }

        public async Task<IReadOnlyList<Dictionary<string, string>>> GetTestsAsync(TestMode? mode)
        {
            await _operationLock.WaitAsync().ConfigureAwait(true);
            try
            {
                TestMode[] modes = mode.HasValue ? new[] { mode.Value } : AllModes;

                List<Dictionary<string, string>> results = new();
                HashSet<string> seen = new(StringComparer.Ordinal);

                foreach (TestMode selectedMode in modes)
                {
                    ITestAdaptor root = await RetrieveTestRootAsync(selectedMode).ConfigureAwait(true);
                    CollectFromNode(root, selectedMode, results, seen, new List<string>());
                }

                return results;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task<TestRunResult> RunTestsAsync(
            TestMode mode,
            TestFilterOptions filterOptions = null,
            TestExecutionOptions executionOptions = null,
            string jobId = null)
        {
            executionOptions ??= new TestExecutionOptions();
            await _operationLock.WaitAsync().ConfigureAwait(true);
            Task<TestRunResult> runTask;
            bool adjustedPlayModeOptions = false;
            bool originalEnterPlayModeOptionsEnabled = false;
            EnterPlayModeOptions originalEnterPlayModeOptions = EnterPlayModeOptions.None;
            try
            {
                if (_runCompletionSource != null && !_runCompletionSource.Task.IsCompleted)
                {
                    throw new InvalidOperationException("A Unity test run is already in progress.");
                }

                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    throw new InvalidOperationException("Cannot start a test run while the Editor is in or entering Play Mode. Stop Play Mode and try again.");
                }

                if (!string.IsNullOrWhiteSpace(jobId) && !TestJobManager.CanDispatch(jobId))
                {
                    throw new OperationCanceledException(
                        $"Test job {jobId} is no longer active; Unity Test Runner dispatch was skipped.");
                }

                if (mode == TestMode.PlayMode &&
                    executionOptions.Fidelity == TestExecutionFidelity.BridgePreserving)
                {
                    adjustedPlayModeOptions = EnsurePlayModeRunsWithoutDomainReload(
                        out originalEnterPlayModeOptionsEnabled,
                        out originalEnterPlayModeOptions);
                }

                HandleDirtyScenes(executionOptions.AllowSceneSave);

                _leafResults.Clear();
                _activeMcpJobId = jobId;
                _runCompletionSource = new TaskCompletionSource<TestRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Mark running immediately so readiness snapshots reflect the busy state even before callbacks fire.
                TestRunStatus.MarkStarted(mode);

                Filter filter = new()
                {
                    testMode = mode,
                    testNames = filterOptions?.TestNames,
                    groupNames = filterOptions?.GroupNames,
                    categoryNames = filterOptions?.CategoryNames,
                    assemblyNames = filterOptions?.AssemblyNames
                };
                ExecutionSettings settings = new(filter);

                if (mode == TestMode.PlayMode &&
                    executionOptions.Fidelity == TestExecutionFidelity.BridgePreserving)
                {
                    TestRunnerNoThrottle.ApplyNoThrottlingPreemptive();
                }

                string unityRunGuid = _testRunnerApi.Execute(settings);
                TestJobManager.OnRunDispatched(jobId, unityRunGuid);

                runTask = _runCompletionSource.Task;
            }
            catch
            {
                // Ensure the status is cleared if we failed to start the run.
                TestRunStatus.MarkFinished();
                _activeMcpJobId = null;
                if (adjustedPlayModeOptions)
                {
                    RestoreEnterPlayModeOptions(originalEnterPlayModeOptionsEnabled, originalEnterPlayModeOptions);
                }

                _operationLock.Release();
                throw;
            }

            try
            {
                return await runTask.ConfigureAwait(true);
            }
            finally
            {
                if (adjustedPlayModeOptions)
                {
                    RestoreEnterPlayModeOptions(originalEnterPlayModeOptionsEnabled, originalEnterPlayModeOptions);
                }

                _activeMcpJobId = null;
                _operationLock.Release();
            }
        }

        public void Dispose()
        {
            if (_callbacksRegistered)
            {
                _callbacksRegistered = false;
                try
                {
                    _testRunnerApi?.UnregisterCallbacks(this);
                }
                catch
                {
                    // Ignore cleanup errors
                }
                finally
                {
                    Interlocked.Decrement(ref _liveCallbackOwnerCount);
                }
            }

            if (_testRunnerApi != null)
            {
                ScriptableObject.DestroyImmediate(_testRunnerApi);
            }

            _operationLock.Dispose();
        }

        private string GetActiveMcpJobId()
        {
            return string.IsNullOrWhiteSpace(_activeMcpJobId)
                ? TestJobManager.CurrentJobId
                : _activeMcpJobId;
        }

        #region TestRunnerApi callbacks

        public void RunStarted(ITestAdaptor testsToRun)
        {
            _leafResults.Clear();
            if (string.IsNullOrWhiteSpace(_activeMcpJobId))
            {
                _activeMcpJobId = TestJobManager.CurrentJobId;
            }
            string jobId = GetActiveMcpJobId();
            try
            {
                List<string> selectedTests = new();
                CollectLeafTestNames(testsToRun, selectedTests);
                TestJobManager.OnRunStarted(jobId, selectedTests);
            }
            catch
            {
                TestJobManager.OnRunStarted(jobId, null);
            }
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            // Always create payload and clean up job state, even if _runCompletionSource is null.
            // This handles domain reload scenarios (e.g., PlayMode tests) where the TestRunnerService
            // is recreated and _runCompletionSource is lost, but TestJobManager state persists via
            // SessionState and the Test Runner still delivers the RunFinished callback.
            TestRunResult payload = TestRunResult.Create(result, _leafResults);
            string jobId = GetActiveMcpJobId();

            // Clean up state regardless of _runCompletionSource - these methods safely handle
            // the case where no MCP job exists (e.g., manual test runs via Unity UI).
            TestJobManager.OnRunFinished(jobId);
            TestJobStatus? validatedOutcome = TestJobManager.FinalizeCurrentJobFromRunFinished(jobId, payload);
            TestRunStatus.MarkFinished(
                payload,
                validatedOutcome.HasValue
                    ? TestJobManager.ToOutcomeString(validatedOutcome.Value)
                    : null);

            // If a domain reload destroyed the original RunTestsAsync caller, the finally block
            // that would normally restore EditorSettings never ran. Restore from SessionState.
            if (_runCompletionSource == null && PlayModeOptionsGuard.IsPending)
            {
                PlayModeOptionsGuard.Restore();
            }

            // Report result to awaiting caller if we have a completion source.
            // The caller's finally block handles restoration in this case.
            if (_runCompletionSource != null)
            {
                _runCompletionSource.TrySetResult(payload);
                _runCompletionSource = null;
            }
            else
            {
                _activeMcpJobId = null;
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
            try
            {
                // Prefer FullName for uniqueness; fall back to Name.
                string fullName = test?.FullName;
                if (string.IsNullOrWhiteSpace(fullName))
                {
                    fullName = test?.Name;
                }
                bool isLeaf = test != null && !test.HasChildren;
                TestJobManager.OnTestStarted(GetActiveMcpJobId(), fullName, isLeaf);
            }
            catch
            {
                // ignore
            }
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (result == null)
            {
                return;
            }

            if (!result.HasChildren)
            {
                _leafResults.Add(result);
                try
                {
                    string fullName = result.Test?.FullName;
                    if (string.IsNullOrWhiteSpace(fullName))
                    {
                        fullName = result.Test?.Name;
                    }

                    bool isFailure = false;
                    string message = null;
                    try
                    {
                        // NUnit outcomes are strings in the adaptor; keep it simple.
                        string outcome = result.ResultState;
                        if (!string.IsNullOrWhiteSpace(outcome))
                        {
                            string normalizedOutcome = outcome.Trim().ToLowerInvariant();
                            isFailure = normalizedOutcome.Contains("failed") || normalizedOutcome.Contains("error");
                        }
                        message = result.Message;
                    }
                    catch
                    {
                        // ignore adaptor quirks
                    }

                    TestJobManager.OnLeafTestFinished(
                        GetActiveMcpJobId(),
                        fullName,
                        result.ResultState,
                        isFailure,
                        message,
                        result.StackTrace,
                        result.Output);
                }
                catch
                {
                    // ignore
                }
            }
        }

        #endregion

        private static int CountLeafTests(ITestAdaptor node)
        {
            if (node == null)
            {
                return 0;
            }

            if (!node.HasChildren)
            {
                return 1;
            }

            int total = 0;
            try
            {
                foreach (var child in node.Children)
                {
                    total += CountLeafTests(child);
                }
            }
            catch
            {
                // If Unity changes the adaptor behavior, treat it as "unknown total".
                return 0;
            }

            return total;
        }

        private static void CollectLeafTestNames(ITestAdaptor node, List<string> output)
        {
            if (node == null)
            {
                return;
            }

            if (!node.HasChildren)
            {
                string fullName = string.IsNullOrWhiteSpace(node.FullName) ? node.Name : node.FullName;
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    output.Add(fullName);
                }
                return;
            }

            if (node.Children == null)
            {
                return;
            }

            foreach (ITestAdaptor child in node.Children)
            {
                CollectLeafTestNames(child, output);
            }
        }

        private static bool EnsurePlayModeRunsWithoutDomainReload(
            out bool originalEnterPlayModeOptionsEnabled,
            out EnterPlayModeOptions originalEnterPlayModeOptions)
        {
            originalEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            originalEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;

            // When Play Mode triggers a domain reload, the MCP connection is torn down and the pending
            // test run response never makes it back to the caller. To keep the bridge alive for this
            // invocation, temporarily enable Enter Play Mode Options with domain reload disabled.
            bool domainReloadDisabled = (originalEnterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0;
            bool needsChange = !originalEnterPlayModeOptionsEnabled || !domainReloadDisabled;
            if (!needsChange)
            {
                return false;
            }

            // Persist originals to SessionState so they survive domain reloads. If the run is
            // interrupted (domain reload, crash), PlayModeOptionsGuard restores them on next load.
            PlayModeOptionsGuard.Save(originalEnterPlayModeOptionsEnabled, originalEnterPlayModeOptions);

            var desired = originalEnterPlayModeOptions | EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = desired;
            return true;
        }

        private static void RestoreEnterPlayModeOptions(bool originalEnabled, EnterPlayModeOptions originalOptions)
        {
            EditorSettings.enterPlayModeOptions = originalOptions;
            EditorSettings.enterPlayModeOptionsEnabled = originalEnabled;
            PlayModeOptionsGuard.Clear();
        }

        private static void HandleDirtyScenes(bool allowSceneSave)
        {
            List<Scene> dirtyScenes = new();
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    dirtyScenes.Add(scene);
                }
            }

            if (dirtyScenes.Count == 0)
            {
                return;
            }

            string dirtySceneNames = string.Join(", ", dirtyScenes.Select(scene =>
                string.IsNullOrWhiteSpace(scene.path) ? $"{scene.name} (unsaved)" : scene.path));
            if (!allowSceneSave)
            {
                throw new TestRunBlockedException(
                    $"dirty_scenes: {dirtySceneNames}. Save them explicitly or rerun with allow_scene_save=true.");
            }

            foreach (Scene scene in dirtyScenes)
            {
                if (string.IsNullOrWhiteSpace(scene.path))
                {
                    throw new TestRunBlockedException(
                        $"unsaved_scene: {scene.name}. Save it explicitly before running tests.");
                }

                if (!EditorSceneManager.SaveScene(scene))
                {
                    throw new TestRunBlockedException($"scene_save_failed: {scene.path}");
                }
            }
        }

        #region Test list helpers

        private async Task<ITestAdaptor> RetrieveTestRootAsync(TestMode mode)
        {
            var tcs = new TaskCompletionSource<ITestAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);

            _testRunnerApi.RetrieveTestList(mode, root =>
            {
                tcs.TrySetResult(root);
            });

            // Ensure the editor pumps at least one additional update in case the window is unfocused.
            EditorApplication.QueuePlayerLoopUpdate();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(true);
            if (completed != tcs.Task)
            {
                throw new TimeoutException($"Timed out waiting for Unity test discovery in {mode} mode.");
            }

            try
            {
                return await tcs.Task.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Unity test discovery failed in {mode} mode: {ex.Message}", ex);
            }
        }

        private static void CollectFromNode(
            ITestAdaptor node,
            TestMode mode,
            List<Dictionary<string, string>> output,
            HashSet<string> seen,
            List<string> path)
        {
            if (node == null)
            {
                return;
            }

            bool hasName = !string.IsNullOrEmpty(node.Name);
            if (hasName)
            {
                path.Add(node.Name);
            }

            bool hasChildren = node.HasChildren && node.Children != null;

            if (!hasChildren)
            {
                string fullName = string.IsNullOrEmpty(node.FullName) ? node.Name ?? string.Empty : node.FullName;
                string key = $"{mode}:{fullName}";

                if (!string.IsNullOrEmpty(fullName) && seen.Add(key))
                {
                    string computedPath = path.Count > 0 ? string.Join("/", path) : fullName;
                    output.Add(new Dictionary<string, string>
                    {
                        ["name"] = node.Name ?? fullName,
                        ["full_name"] = fullName,
                        ["path"] = computedPath,
                        ["mode"] = mode.ToString(),
                    });
                }
            }
            else if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    CollectFromNode(child, mode, output, seen, path);
                }
            }

            if (hasName && path.Count > 0)
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        #endregion
    }

    /// <summary>
    /// Summary of a Unity test run.
    /// </summary>
    public sealed class TestRunResult
    {
        internal TestRunResult(TestRunSummary summary, IReadOnlyList<TestRunTestResult> results)
        {
            Summary = summary;
            Results = results;
        }

        public TestRunSummary Summary { get; }
        public IReadOnlyList<TestRunTestResult> Results { get; }

        public int Total => Summary.Total;
        public int Passed => Summary.Passed;
        public int Failed => Summary.Failed;
        public int Skipped => Summary.Skipped;

        public object ToSerializable(string mode, bool includeDetails = false, bool includeFailedTests = false)
        {
            // Determine which results to include
            IEnumerable<object> resultsToSerialize;
            if (includeDetails)
            {
                // Include all test results
                resultsToSerialize = Results.Select(r => r.ToSerializable());
            }
            else if (includeFailedTests)
            {
                // Include only failed and skipped tests
                resultsToSerialize = Results
                    .Where(r => !string.Equals(r.State, "Passed", StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.ToSerializable());
            }
            else
            {
                // No individual test results
                resultsToSerialize = null;
            }

            return new
            {
                mode,
                summary = Summary.ToSerializable(),
                results = resultsToSerialize?.ToList(),
            };
        }

        internal static TestRunResult Create(ITestResultAdaptor summary, IReadOnlyList<ITestResultAdaptor> tests)
        {
            var materializedTests = tests.Select(TestRunTestResult.FromAdaptor).ToList();

            int passed = summary?.PassCount
                ?? materializedTests.Count(t => string.Equals(t.State, "Passed", StringComparison.OrdinalIgnoreCase));
            int failed = summary?.FailCount
                ?? materializedTests.Count(t => string.Equals(t.State, "Failed", StringComparison.OrdinalIgnoreCase));
            int skipped = summary?.SkipCount
                ?? materializedTests.Count(t => string.Equals(t.State, "Skipped", StringComparison.OrdinalIgnoreCase));

            double duration = summary?.Duration
                ?? materializedTests.Sum(t => t.DurationSeconds);

            int total = summary != null ? passed + failed + skipped : materializedTests.Count;

            var summaryPayload = new TestRunSummary(
                total,
                passed,
                failed,
                skipped,
                duration,
                summary?.ResultState ?? "Unknown");

            return new TestRunResult(summaryPayload, materializedTests);
        }
    }

    public sealed class TestRunSummary
    {
        internal TestRunSummary(int total, int passed, int failed, int skipped, double durationSeconds, string resultState)
        {
            Total = total;
            Passed = passed;
            Failed = failed;
            Skipped = skipped;
            DurationSeconds = durationSeconds;
            ResultState = resultState;
        }

        public int Total { get; }
        public int Passed { get; }
        public int Failed { get; }
        public int Skipped { get; }
        public double DurationSeconds { get; }
        public string ResultState { get; }

        internal object ToSerializable()
        {
            return new
            {
                total = Total,
                passed = Passed,
                failed = Failed,
                skipped = Skipped,
                durationSeconds = DurationSeconds,
                resultState = ResultState,
            };
        }
    }

    public sealed class TestRunTestResult
    {
        internal TestRunTestResult(
            string name,
            string fullName,
            string state,
            double durationSeconds,
            string message,
            string stackTrace,
            string output)
        {
            Name = name;
            FullName = fullName;
            State = state;
            DurationSeconds = durationSeconds;
            Message = message;
            StackTrace = stackTrace;
            Output = output;
        }

        public string Name { get; }
        public string FullName { get; }
        public string State { get; }
        public double DurationSeconds { get; }
        public string Message { get; }
        public string StackTrace { get; }
        public string Output { get; }

        internal object ToSerializable()
        {
            return new
            {
                name = Name,
                fullName = FullName,
                state = State,
                durationSeconds = DurationSeconds,
                message = Message,
                stackTrace = StackTrace,
                output = Output,
            };
        }

        internal static TestRunTestResult FromAdaptor(ITestResultAdaptor adaptor)
        {
            if (adaptor == null)
            {
                return new TestRunTestResult(string.Empty, string.Empty, "Unknown", 0.0, string.Empty, string.Empty, string.Empty);
            }

            return new TestRunTestResult(
                adaptor.Name,
                adaptor.FullName,
                adaptor.ResultState,
                adaptor.Duration,
                adaptor.Message,
                adaptor.StackTrace,
                adaptor.Output);
        }
    }
}
