using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine.SceneManagement;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Tests for TestJobManager's per-job InitTimeoutMs feature.
    /// Uses reflection to manipulate internal state since StartJob triggers a real test run.
    /// </summary>
    public class TestJobManagerInitTimeoutTests
    {
        private FieldInfo _jobsField;
        private FieldInfo _currentJobIdField;
        private MethodInfo _getJobMethod;
        private MethodInfo _persistMethod;
        private MethodInfo _restoreMethod;
        private Type _testJobType;

        private string _originalJobId;
        private string _artifactDirectory;

        [SetUp]
        public void SetUp()
        {
            var asm = typeof(MCPServiceLocator).Assembly;
            var managerType = asm.GetType("MCPForUnity.Editor.Services.TestJobManager");
            Assert.NotNull(managerType, "Could not find TestJobManager");

            _testJobType = asm.GetType("MCPForUnity.Editor.Services.TestJob");
            Assert.NotNull(_testJobType, "Could not find TestJob");

            _jobsField = managerType.GetField("Jobs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_jobsField, "Could not find Jobs field");

            _currentJobIdField = managerType.GetField("_currentJobId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_currentJobIdField, "Could not find _currentJobId field");

            _getJobMethod = managerType.GetMethod("GetJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_getJobMethod, "Could not find GetJob method");

            _persistMethod = managerType.GetMethod("PersistToSessionState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_persistMethod, "Could not find PersistToSessionState method");

            _restoreMethod = managerType.GetMethod("TryRestoreFromSessionState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(_restoreMethod, "Could not find TryRestoreFromSessionState method");

            // Snapshot original state
            _originalJobId = _currentJobIdField.GetValue(null) as string;
            // We'll restore _currentJobId in TearDown; Jobs dictionary is shared static state
        }

        [TearDown]
        public void TearDown()
        {
            // Restore original state
            _currentJobIdField.SetValue(null, _originalJobId);
            // Clean up any test jobs we inserted
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            jobs?.Remove("test-init-timeout-job");
            jobs?.Remove("test-init-timeout-default");
            jobs?.Remove("test-init-timeout-persist");
            jobs?.Remove("test-init-timeout-reload-grace");
            jobs?.Remove("test-init-timeout-artifact");
            jobs?.Remove("test-callback-old-job");
            jobs?.Remove("test-callback-current-job");
            // Flush cleaned state to SessionState so synthetic jobs don't survive domain reloads.
            // The persist test writes to SessionState; without this, the stub job would be
            // restored on the next [InitializeOnLoadMethod] and pollute later test runs.
            _persistMethod.Invoke(null, new object[] { true });
            if (!string.IsNullOrWhiteSpace(_artifactDirectory) && Directory.Exists(_artifactDirectory))
            {
                Directory.Delete(_artifactDirectory, true);
            }
        }

        [Test]
        public void TryResolveInitializationTimeout_UsesModeSpecificDefaultsAndRejectsAmbiguousValues()
        {
            bool editValid = TestJobManager.TryResolveInitializationTimeout(
                TestMode.EditMode,
                0,
                out long editTimeout,
                out string editError);
            bool playValid = TestJobManager.TryResolveInitializationTimeout(
                TestMode.PlayMode,
                0,
                out long playTimeout,
                out string playError);
            bool ambiguousValid = TestJobManager.TryResolveInitializationTimeout(
                TestMode.EditMode,
                60,
                out _,
                out string ambiguousError);

            Assert.IsTrue(editValid, editError);
            Assert.AreEqual(15_000L, editTimeout);
            Assert.IsTrue(playValid, playError);
            Assert.AreEqual(120_000L, playTimeout);
            Assert.IsFalse(ambiguousValid);
            StringAssert.Contains("Use 60000 for 60 seconds", ambiguousError);
        }

        [Test]
        public void GetJob_WithCustomInitTimeout_UsesPerJobTimeout()
        {
            // Arrange: insert a job with a custom init timeout and a start time far enough in the
            // past to exceed the default 15s but within the custom 120s.
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-job");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "PlayMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 30_000); // 30s ago
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 30_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, null); // Not initialized yet
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 120_000L); // 120s custom timeout
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-job"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-job");

            // Act: GetJob should NOT auto-fail because 30s < 120s custom timeout
            var result = _getJobMethod.Invoke(null, new object[] { "test-init-timeout-job" });

            // Assert: job should still be running
            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.Running, status,
                "Job with 120s custom timeout should not auto-fail after 30s");
        }

        [Test]
        public void GetJob_WithDefaultTimeout_AutoFailsAfter15Seconds()
        {
            // Arrange: insert a job with InitTimeoutMs=0 (use default) and start time 20s ago
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-default");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "EditMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 20_000); // 20s ago
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 20_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, null);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 0L); // Use default
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-default"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-default");

            // Act: GetJob should auto-fail because 20s > 15s default
            var result = _getJobMethod.Invoke(null, new object[] { "test-init-timeout-default" });

            // Assert: an initialization timeout is infrastructure failure, not a test assertion failure.
            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);
            Assert.AreEqual(TestJobStatus.InfrastructureError, status,
                "Job with default timeout should auto-fail after 20s");
            Assert.AreEqual("test-init-timeout-default", _currentJobIdField.GetValue(null));
            Assert.IsTrue((bool)_testJobType.GetProperty("InitializationCleanupPending").GetValue(result));
            Assert.IsFalse(TestJobManager.CanDispatch("test-init-timeout-default"));
        }

        [Test]
        public void InitializationTimeout_PersistsTerminalArtifactAndTimelineEvent()
        {
            System.Collections.IDictionary jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _artifactDirectory = Path.Combine(
                Path.GetTempPath(),
                $"unity-mcp-test-job-{Guid.NewGuid():N}");

            object job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-artifact");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "EditMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 20_000);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now - 20_000);
            _testJobType.GetProperty("InitializationIdleSinceUnixMs").SetValue(job, now - 20_000);
            _testJobType.GetProperty("TotalTests").SetValue(job, null);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 15_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());
            _testJobType.GetProperty("ExpectedTestNames").SetValue(job, new List<string>());
            _testJobType.GetProperty("SelectedTestNames").SetValue(job, new List<string>());
            _testJobType.GetProperty("MissingExpectedTests").SetValue(job, new List<string>());
            _testJobType.GetProperty("ArtifactDirectory").SetValue(job, _artifactDirectory);

            jobs["test-init-timeout-artifact"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-artifact");

            object result = _getJobMethod.Invoke(null, new[] { "test-init-timeout-artifact" });
            string runPath = Path.Combine(_artifactDirectory, "run.json");
            string timelinePath = Path.Combine(_artifactDirectory, "timeline.jsonl");
            JObject run = JObject.Parse(File.ReadAllText(runPath));

            Assert.AreEqual(TestJobStatus.InfrastructureError,
                (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result));
            Assert.AreEqual("infrastructure_error", run["status"]?.ToString());
            Assert.AreEqual("cleanup_pending", run["initialization"]?["phase"]?.ToString());
            Assert.IsTrue(run["initialization"]?["cleanup_pending"]?.Value<bool>());
            StringAssert.Contains("\"event\":\"initialization_timed_out\"", File.ReadAllText(timelinePath));
        }

        [Test]
        public void JobCorrelatedCallback_DoesNotMutateAnotherCurrentJob()
        {
            System.Collections.IDictionary jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            object oldJob = CreateSyntheticRunningJob("test-callback-old-job", now);
            object currentJob = CreateSyntheticRunningJob("test-callback-current-job", now);
            jobs["test-callback-old-job"] = oldJob;
            jobs["test-callback-current-job"] = currentJob;
            _currentJobIdField.SetValue(null, "test-callback-current-job");

            TestJobManager.OnRunStarted("test-callback-old-job", new[] { "Suite.Old" });

            Assert.IsNull(_testJobType.GetProperty("TotalTests").GetValue(oldJob));
            Assert.IsNull(_testJobType.GetProperty("TotalTests").GetValue(currentJob));
        }

        private object CreateSyntheticRunningJob(string jobId, long now)
        {
            object job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, jobId);
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "EditMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 15_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());
            _testJobType.GetProperty("ExpectedTestNames").SetValue(job, new List<string>());
            _testJobType.GetProperty("SelectedTestNames").SetValue(job, new List<string>());
            _testJobType.GetProperty("MissingExpectedTests").SetValue(job, new List<string>());
            return job;
        }

        [Test]
        public void InitTimeoutMs_SurvivesPersistAndRestore()
        {
            // Arrange: insert a job with custom InitTimeoutMs
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-persist");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "PlayMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now);
            _testJobType.GetProperty("TotalTests").SetValue(job, null);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 90_000L);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-persist"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-persist");

            // Act: persist then restore (simulates domain reload)
            _persistMethod.Invoke(null, new object[] { true });
            // Clear in-memory state
            jobs.Remove("test-init-timeout-persist");
            _currentJobIdField.SetValue(null, null);
            // Restore from SessionState
            _restoreMethod.Invoke(null, null);

            // Assert: restored job should have the same InitTimeoutMs
            var restoredJobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            Assert.IsTrue(restoredJobs.Contains("test-init-timeout-persist"),
                "Job should be restored from SessionState");

            var restoredJob = restoredJobs["test-init-timeout-persist"];
            var restoredTimeout = (long)_testJobType.GetProperty("InitTimeoutMs").GetValue(restoredJob);
            Assert.AreEqual(90_000L, restoredTimeout,
                "InitTimeoutMs should survive persist/restore cycle");
        }

        [Test]
        public void InitializationTimeout_AfterDomainReload_RestartsFromContinuousEditorReadiness()
        {
            var jobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var job = Activator.CreateInstance(_testJobType);
            _testJobType.GetProperty("JobId").SetValue(job, "test-init-timeout-reload-grace");
            _testJobType.GetProperty("Status").SetValue(job, TestJobStatus.Running);
            _testJobType.GetProperty("Mode").SetValue(job, "EditMode");
            _testJobType.GetProperty("StartedUnixMs").SetValue(job, now - 60_000);
            _testJobType.GetProperty("LastUpdateUnixMs").SetValue(job, now);
            _testJobType.GetProperty("TotalTests").SetValue(job, null);
            _testJobType.GetProperty("InitTimeoutMs").SetValue(job, 15_000L);
            _testJobType.GetProperty("UnityRunGuid").SetValue(job, "test-unity-run-guid");
            _testJobType.GetProperty("InitializationIdleSinceUnixMs").SetValue(job, now - 60_000);
            _testJobType.GetProperty("FailuresSoFar").SetValue(job, new List<TestJobFailure>());

            jobs["test-init-timeout-reload-grace"] = job;
            _currentJobIdField.SetValue(null, "test-init-timeout-reload-grace");
            _persistMethod.Invoke(null, new object[] { true });
            jobs.Remove("test-init-timeout-reload-grace");
            _currentJobIdField.SetValue(null, null);

            long restoreStarted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _restoreMethod.Invoke(null, null);

            var restoredJobs = _jobsField.GetValue(null) as System.Collections.IDictionary;
            var restoredJob = restoredJobs["test-init-timeout-reload-grace"];
            long idleSince = (long)_testJobType.GetProperty("InitializationIdleSinceUnixMs").GetValue(restoredJob);
            bool resumed = (bool)_testJobType.GetProperty("ResumedAfterDomainReload").GetValue(restoredJob);
            string unityRunGuid = (string)_testJobType.GetProperty("UnityRunGuid").GetValue(restoredJob);
            var result = _getJobMethod.Invoke(null, new object[] { "test-init-timeout-reload-grace" });
            var status = (TestJobStatus)_testJobType.GetProperty("Status").GetValue(result);

            Assert.GreaterOrEqual(idleSince, restoreStarted);
            Assert.IsTrue(resumed);
            Assert.AreEqual("test-unity-run-guid", unityRunGuid);
            Assert.AreEqual(TestJobStatus.Running, status,
                "A restored job must receive a fresh continuous-idle initialization window.");
        }
    }

    public class TestJobManagerOutcomeTests
    {
        private static TestRunResult Result(
            int total,
            int passed,
            int failed,
            int skipped,
            string state)
        {
            return new TestRunResult(
                new TestRunSummary(total, passed, failed, skipped, 0.1, state),
                Array.Empty<TestRunTestResult>());
        }

        private static TestJob Job(int selected, int completed)
        {
            return new TestJob
            {
                TotalTests = selected,
                CompletedTests = completed,
                MinimumExpectedTests = 1,
                FailOnSkipped = true,
                SelectedTestNames = new List<string> { "Suite.Test" },
                ExpectedTestNames = new List<string>()
            };
        }

        [Test]
        public void EvaluateOutcome_ZeroTests_IsNoTests()
        {
            TestJobStatus status = TestJobManager.EvaluateOutcome(
                Job(0, 0), Result(0, 0, 0, 0, "Passed"), out _, out _);

            Assert.AreEqual(TestJobStatus.NoTests, status);
        }

        [Test]
        public void EvaluateOutcome_SkippedTests_IsSkipped()
        {
            TestJobStatus status = TestJobManager.EvaluateOutcome(
                Job(1, 1), Result(1, 0, 0, 1, "Skipped"), out _, out _);

            Assert.AreEqual(TestJobStatus.Skipped, status);
        }

        [Test]
        public void EvaluateOutcome_IncompleteRun_IsAborted()
        {
            TestJobStatus status = TestJobManager.EvaluateOutcome(
                Job(2, 1), Result(1, 1, 0, 0, "Passed"), out _, out _);

            Assert.AreEqual(TestJobStatus.Aborted, status);
        }

        [Test]
        public void EvaluateOutcome_MissingExpectedTest_IsBlocked()
        {
            TestJob job = Job(1, 1);
            job.ExpectedTestNames = new List<string> { "Suite.Required" };

            TestJobStatus status = TestJobManager.EvaluateOutcome(
                job, Result(1, 1, 0, 0, "Passed"), out _, out List<string> missing);

            Assert.AreEqual(TestJobStatus.Blocked, status);
            CollectionAssert.AreEqual(new[] { "Suite.Required" }, missing);
        }

        [Test]
        public void EvaluateOutcome_CompletePassingRun_IsPassed()
        {
            TestJobStatus status = TestJobManager.EvaluateOutcome(
                Job(1, 1), Result(1, 1, 0, 0, "Passed"), out string error, out _);

            Assert.AreEqual(TestJobStatus.Passed, status);
            Assert.IsNull(error);
        }
    }

    public class TestRunnerServiceDirtySceneTests
    {
        [Test]
        public void HandleDirtyScenes_WithoutExplicitPermission_BlocksAndDoesNotSave()
        {
            Scene originalScene = SceneManager.GetActiveScene();
            Assert.IsFalse(originalScene.isDirty, "This test will not replace a dirty user scene.");
            string originalPath = originalScene.path;
            string temporaryPath = $"Assets/MCPForUnity_DirtySceneGuard_{Guid.NewGuid():N}.unity";
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.IsTrue(EditorSceneManager.SaveScene(scene, temporaryPath));
            EditorSceneManager.MarkSceneDirty(scene);
            MethodInfo handleDirtyScenes = typeof(TestRunnerService).GetMethod(
                "HandleDirtyScenes",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(handleDirtyScenes);

            try
            {
                TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(
                    () => handleDirtyScenes.Invoke(null, new object[] { false }));
                Assert.IsInstanceOf<TestRunBlockedException>(invocation.InnerException);
                Assert.IsTrue(scene.isDirty, "The guard must not save or clear the dirty scene.");
                Assert.AreEqual(temporaryPath, scene.path);
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(temporaryPath);
                if (!string.IsNullOrWhiteSpace(originalPath))
                {
                    EditorSceneManager.OpenScene(originalPath, OpenSceneMode.Single);
                }
            }
        }
    }
}
