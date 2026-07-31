using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Services
{
    public class ServerRuntimeInstallerTests
    {
        [TestCase(RuntimePlatform.WindowsEditor)]
        [TestCase(RuntimePlatform.LinuxEditor)]
        [TestCase(RuntimePlatform.OSXEditor)]
        public void BuildVenvArguments_AlwaysCreatesRelocatableEnvironment(
            RuntimePlatform platform)
        {
            string arguments = ServerRuntimeInstaller.BuildVenvArguments(
                @"C:\runtime path\staging",
                platform);

            StringAssert.Contains("--relocatable", arguments);
        }

        [Test]
        public void BuildVenvArguments_OnWindowsUsesManagedPython()
        {
            string arguments = ServerRuntimeInstaller.BuildVenvArguments(
                @"C:\runtime path\staging",
                RuntimePlatform.WindowsEditor);

            StringAssert.Contains("--managed-python", arguments);
            StringAssert.Contains("--python 3.12", arguments);
            StringAssert.Contains("\"C:\\runtime path\\staging\"", arguments);
        }

        [Test]
        public void BuildRuntimeManifest_UsesFinalRuntimeExecutablePaths()
        {
            InstalledServerRuntime runtime = new InstalledServerRuntime(
                @"C:\project\Library\MCPForUnity\ServerRuntime\final",
                @"C:\project\Library\MCPForUnity\ServerRuntime\final\Scripts\mcp-for-unity.exe",
                @"C:\project\Library\MCPForUnity\ServerRuntime\final\Scripts\mcp-for-unity-supervisor.exe",
                @"C:\project\Library\MCPForUnity\ServerRuntime\final\runtime.json",
                "10.1.1-beta.2",
                @"C:\source\Server");

            Newtonsoft.Json.Linq.JObject manifest = ServerRuntimeInstaller.BuildRuntimeManifest(
                runtime,
                runtime.Version,
                runtime.Source,
                "Python 3.12.13",
                true);

            Assert.AreEqual(runtime.ServerExecutable, manifest["serverExecutable"]?.ToString());
            Assert.AreEqual(
                runtime.SupervisorExecutable,
                manifest["supervisorExecutable"]?.ToString());
            StringAssert.DoesNotContain(".staging-", manifest.ToString());
        }

        [Test]
        public async Task MoveDirectoryWithRetryAsync_RetriesTransientAccessDenial()
        {
            int attempts = 0;
            int delays = 0;

            await ServerRuntimeInstaller.MoveDirectoryWithRetryAsync(
                "staging",
                "target",
                CancellationToken.None,
                4,
                1,
                4,
                (_, _) =>
                {
                    attempts++;
                    if (attempts < 3)
                    {
                        throw new UnauthorizedAccessException("runtime is still in use");
                    }
                },
                (_, _) =>
                {
                    delays++;
                    return Task.CompletedTask;
                });

            Assert.AreEqual(3, attempts);
            Assert.AreEqual(2, delays);
        }

        [Test]
        public void MoveDirectoryWithRetryAsync_ReportsBoundedFailure()
        {
            int attempts = 0;

            IOException exception = Assert.ThrowsAsync<IOException>(async () =>
                await ServerRuntimeInstaller.MoveDirectoryWithRetryAsync(
                    "staging",
                    "target",
                    CancellationToken.None,
                    3,
                    1,
                    4,
                    (_, _) =>
                    {
                        attempts++;
                        throw new UnauthorizedAccessException("runtime is still in use");
                    },
                    (_, _) => Task.CompletedTask));

            Assert.AreEqual(3, attempts);
            StringAssert.Contains("after 3 attempts", exception.Message);
            Assert.IsInstanceOf<UnauthorizedAccessException>(exception.InnerException);
        }

        [TestCase(-1f, 0f)]
        [TestCase(0.4f, 0.4f)]
        [TestCase(2f, 1f)]
        public void ServerStartProgress_ClampsNormalizedProgress(
            float input,
            float expected)
        {
            var update = new ServerStartProgress(input, null);

            Assert.AreEqual(expected, update.NormalizedProgress);
            Assert.AreEqual(string.Empty, update.Message);
        }
    }
}
