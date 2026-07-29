using System;
using System.IO;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MCPForUnityTests.Editor.Services.Server
{
    [TestFixture]
    public class InstalledServerCommandBuilderTests
    {
        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "McpInstalledCommandTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, true);
            EditorPrefs.SetString(EditorPrefKeys.HttpBaseUrl, "http://127.0.0.1:8080");
            EditorPrefs.SetInt(EditorPrefKeys.ServerMemorySoftLimitMb, 512);
            EditorPrefs.SetBool(EditorPrefKeys.ServerMemoryHardLimitEnabled, true);
            EditorPrefs.SetInt(EditorPrefKeys.ServerMemoryHardLimitMb, 768);
            EditorPrefs.SetInt(EditorPrefKeys.ServerSessionIdleTimeoutSeconds, 1800);
            EditorPrefs.SetInt(EditorPrefKeys.ServerMaxSessions, 64);
            EditorConfigurationCache.Instance.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }

        [Test]
        public void InstalledCommand_IncludesSessionAndMemoryBounds()
        {
            string extension = Application.platform == RuntimePlatform.WindowsEditor
                ? ".exe"
                : string.Empty;
            string server = Path.Combine(_tempDirectory, "mcp-for-unity" + extension);
            string supervisor = Path.Combine(
                _tempDirectory,
                "mcp-for-unity-supervisor" + extension);
            File.WriteAllText(server, string.Empty);
            File.WriteAllText(supervisor, string.Empty);
            var runtime = new InstalledServerRuntime(
                _tempDirectory,
                server,
                supervisor,
                Path.Combine(_tempDirectory, "runtime.json"),
                "1.0",
                "test");

            bool success = new ServerCommandBuilder().TryBuildInstalledCommand(
                runtime,
                123,
                8080,
                Path.Combine(_tempDirectory, "state.json"),
                Path.Combine(_tempDirectory, "server.pid"),
                "token",
                out string command,
                out string error);

            Assert.IsTrue(success, error);
            StringAssert.Contains("--http-session-idle-timeout 1800", command);
            StringAssert.Contains("--http-max-sessions 64", command);
            StringAssert.Contains("--unity-instance-token token", command);
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                StringAssert.Contains("--soft-memory-limit-mb 512", command);
                StringAssert.Contains("--hard-memory-limit-mb 768", command);
                StringAssert.Contains("--parent-pid 123", command);
                StringAssert.Contains("--runtime-version 1.0", command);
            }
        }
    }
}
