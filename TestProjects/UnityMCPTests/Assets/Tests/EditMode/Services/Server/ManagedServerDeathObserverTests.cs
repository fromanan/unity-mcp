using System;
using System.IO;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Server;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services.Server
{
    [TestFixture]
    public class ManagedServerDeathObserverTests
    {
        [Test]
        public void ShouldReportUnexpectedExit_RequiresCurrentUnclassifiedGeneration()
        {
            ManagedServerStatus status = CreateStatus();

            Assert.IsFalse(ManagedServerDeathObserver.ShouldReportUnexpectedExit(
                status, 300, 900.0, true, 2, false));
            Assert.IsFalse(ManagedServerDeathObserver.ShouldReportUnexpectedExit(
                status, 301, 900.0, false, 2, false));
            Assert.IsFalse(ManagedServerDeathObserver.ShouldReportUnexpectedExit(
                status, 300, 1002.1, false, 2, false));
            Assert.IsFalse(ManagedServerDeathObserver.ShouldReportUnexpectedExit(
                status, 300, 900.0, false, 1, false));
            Assert.IsFalse(ManagedServerDeathObserver.ShouldReportUnexpectedExit(
                status, 300, 900.0, false, 2, true));
            Assert.IsFalse(ManagedServerDeathObserver.ShouldReportUnexpectedExit(
                status, 300, 900.0, false, 2, false, true));

            status.ExitReason = "server_exited";
            Assert.IsFalse(ManagedServerDeathObserver.ShouldReportUnexpectedExit(
                status, 300, 900.0, false, 2, false));

            status.ExitReason = null;
            Assert.IsTrue(ManagedServerDeathObserver.ShouldReportUnexpectedExit(
                status, 300, 900.0, false, 2, false));
        }

        [Test]
        public void ShouldRunObserver_SkipsUnapprovedBatchMode()
        {
            Assert.IsTrue(ManagedServerDeathObserver.ShouldRunObserver(false, null));
            Assert.IsFalse(ManagedServerDeathObserver.ShouldRunObserver(true, null));
            Assert.IsTrue(ManagedServerDeathObserver.ShouldRunObserver(true, "1"));
        }

        [Test]
        public void ShutdownSuppression_BecomesGenerationDurableAfterConfirmedExit()
        {
            Assert.IsFalse(ManagedServerDeathObserver.ShouldPersistShutdownSuppression(
                true,
                1,
                false));
            Assert.IsTrue(ManagedServerDeathObserver.ShouldPersistShutdownSuppression(
                true,
                2,
                false));
            Assert.IsFalse(ManagedServerDeathObserver.ShouldPersistShutdownSuppression(
                true,
                2,
                true));
        }

        [Test]
        public void TryReadPath_ParsesDurableSupervisorStateWithoutEditorPrefs()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "McpServerState-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(
                path,
                @"{""schema_version"":1,""supervisor_pid"":100,""server_pid"":200," +
                @"""unity_pid"":300,""port"":8080,""launched_at_unix"":1000.0," +
                @"""peak_job_memory_bytes"":4096,""exit_reason"":null}");

            try
            {
                Assert.IsTrue(ServerRunStateReader.TryReadPath(
                    path,
                    out ManagedServerStatus status));
                Assert.AreEqual(100, status.SupervisorPid);
                Assert.AreEqual(200, status.ServerPid);
                Assert.AreEqual(300, status.UnityPid);
                Assert.AreEqual(8080, status.Port);
                Assert.AreEqual(4096, status.PeakJobMemoryBytes);
                Assert.IsNull(status.ExitReason);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void ManagedSupervisorExitLine_PreservesProcessAndStateClassification()
        {
            ManagedServerStatus status = CreateStatus();
            status.ServerExitCode = 7;
            status.ExitReason = "server_exited";

            string line = ServerManagementService.BuildManagedSupervisorExitLine(
                new DateTimeOffset(2026, 9, 5, 2, 0, 0, TimeSpan.Zero),
                100,
                7,
                status);

            StringAssert.Contains("timestamp_utc=2026-09-05T02:00:00.0000000+00:00", line);
            StringAssert.Contains("supervisor_pid=100", line);
            StringAssert.Contains("process_exit_code=7", line);
            StringAssert.Contains("state_exit_reason=server_exited", line);
            StringAssert.Contains("server_pid=200", line);
            StringAssert.Contains("server_exit_code=7", line);
        }

        [Test]
        public void LifecycleEventLine_BindsShutdownRequestToSupervisorGeneration()
        {
            ManagedServerStatus status = CreateStatus();
            string line = ServerRunStateReader.BuildLifecycleEventLine(
                status,
                "shutdown_requested",
                "user_stop_requested",
                status.ServerPid,
                new DateTimeOffset(2026, 9, 5, 2, 0, 0, TimeSpan.Zero));

            JObject lifecycleEvent = JObject.Parse(line);
            Assert.AreEqual("shutdown_requested", lifecycleEvent.Value<string>("event"));
            Assert.AreEqual("100:1000", lifecycleEvent.Value<string>("generation"));
            Assert.AreEqual("user_stop_requested", lifecycleEvent.Value<string>("reason"));
            Assert.AreEqual(200, lifecycleEvent.Value<int>("target_process_id"));
        }

        [Test]
        public void HasRecentShutdownRequest_RequiresMatchingGenerationAndFreshTimestamp()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "McpServerLifecycle-" + Guid.NewGuid().ToString("N") + ".jsonl");
            ManagedServerStatus status = CreateStatus();
            DateTimeOffset nowUtc = new DateTimeOffset(
                2026,
                9,
                5,
                2,
                1,
                0,
                TimeSpan.Zero);

            try
            {
                ManagedServerStatus oldGeneration = CreateStatus();
                oldGeneration.SupervisorPid = 99;
                File.WriteAllLines(
                    path,
                    new[]
                    {
                        "{not-json",
                        ServerRunStateReader.BuildLifecycleEventLine(
                            status,
                            "shutdown_requested",
                            "stale",
                            status.ServerPid,
                            nowUtc - TimeSpan.FromMinutes(2)),
                        ServerRunStateReader.BuildLifecycleEventLine(
                            oldGeneration,
                            "shutdown_requested",
                            "wrong_generation",
                            oldGeneration.ServerPid,
                            nowUtc),
                        ServerRunStateReader.BuildLifecycleEventLine(
                            status,
                            "shutdown_requested",
                            "managed_stop_requested",
                            status.SupervisorPid,
                            nowUtc - TimeSpan.FromSeconds(10))
                    });

                Assert.IsTrue(ServerRunStateReader.HasRecentShutdownRequest(
                    path,
                    status,
                    nowUtc,
                    TimeSpan.FromMinutes(1)));
                Assert.IsFalse(ServerRunStateReader.HasRecentShutdownRequest(
                    path,
                    oldGeneration,
                    nowUtc + TimeSpan.FromMinutes(2),
                    TimeSpan.FromMinutes(1)));
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static ManagedServerStatus CreateStatus()
        {
            return new ManagedServerStatus
            {
                SupervisorPid = 100,
                ServerPid = 200,
                UnityPid = 300,
                Port = 8080,
                LaunchedAtUnix = 1000.0
            };
        }
    }
}
