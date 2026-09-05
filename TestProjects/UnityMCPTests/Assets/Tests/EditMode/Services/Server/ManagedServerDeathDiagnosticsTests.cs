using System;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Server;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services.Server
{
    [TestFixture]
    public class ManagedServerDeathDiagnosticsTests
    {
        [Test]
        public void LifecycleEventLine_IncludesObservedExitAndSystemPressure()
        {
            ManagedServerStatus status = new ManagedServerStatus
            {
                SupervisorPid = 100,
                ServerPid = 200,
                UnityPid = 300,
                Port = 8080,
                LaunchedAtUnix = 1000.0
            };

            string line = ServerRunStateReader.BuildLifecycleEventLine(
                status,
                "supervisor_exited_unclassified",
                "unexpected exit",
                status.SupervisorPid,
                new DateTimeOffset(2026, 9, 5, 2, 0, 0, TimeSpan.Zero),
                observedSupervisorExitCode: -1073741819,
                unityPrivateBytes: 32L * 1024L * 1024L * 1024L,
                systemCommitUsedPercent: 96,
                systemAvailablePhysicalBytes: 1024L * 1024L * 1024L,
                systemCommitUsedBytes: 63L * 1024L * 1024L * 1024L,
                systemCommitLimitBytes: 65L * 1024L * 1024L * 1024L);

            JObject lifecycleEvent = JObject.Parse(line);
            Assert.AreEqual(-1073741819, lifecycleEvent.Value<int>("observed_supervisor_exit_code"));
            Assert.AreEqual(32L * 1024L * 1024L * 1024L, lifecycleEvent.Value<long>("unity_private_bytes"));
            Assert.AreEqual(96, lifecycleEvent.Value<int>("system_commit_used_percent"));
            Assert.AreEqual(1024L * 1024L * 1024L, lifecycleEvent.Value<long>("system_available_physical_bytes"));
            Assert.AreEqual(63L * 1024L * 1024L * 1024L, lifecycleEvent.Value<long>("system_commit_used_bytes"));
            Assert.AreEqual(65L * 1024L * 1024L * 1024L, lifecycleEvent.Value<long>("system_commit_limit_bytes"));
        }
    }
}
