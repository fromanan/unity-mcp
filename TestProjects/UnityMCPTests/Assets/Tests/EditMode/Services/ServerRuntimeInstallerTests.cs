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
            var runtime = new InstalledServerRuntime(
                @"C:\project\Library\MCPForUnity\ServerRuntime\final",
                @"C:\project\Library\MCPForUnity\ServerRuntime\final\Scripts\mcp-for-unity.exe",
                @"C:\project\Library\MCPForUnity\ServerRuntime\final\Scripts\mcp-for-unity-supervisor.exe",
                @"C:\project\Library\MCPForUnity\ServerRuntime\final\runtime.json",
                "10.1.1-beta.2",
                @"C:\source\Server");

            var manifest = ServerRuntimeInstaller.BuildRuntimeManifest(
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
    }
}
