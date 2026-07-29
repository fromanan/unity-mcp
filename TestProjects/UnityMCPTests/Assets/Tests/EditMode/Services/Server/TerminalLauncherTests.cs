using System;
using NUnit.Framework;
using MCPForUnity.Editor.Services.Server;

namespace MCPForUnityTests.Editor.Services.Server
{
    /// <summary>
    /// Unit tests for TerminalLauncher component.
    /// Note: Tests avoid actually launching terminals to prevent test instability.
    /// </summary>
    [TestFixture]
    public class TerminalLauncherTests
    {
        private TerminalLauncher _launcher;

        [SetUp]
        public void SetUp()
        {
            _launcher = new TerminalLauncher();
        }

        #region GetProjectRootPath Tests

        [Test]
        public void GetProjectRootPath_ReturnsNonEmpty()
        {
            // Act
            string path = _launcher.GetProjectRootPath();

            // Assert
            Assert.IsNotNull(path);
            Assert.IsNotEmpty(path);
        }

        [Test]
        public void GetProjectRootPath_ReturnsValidDirectory()
        {
            // Act
            string path = _launcher.GetProjectRootPath();

            // Assert
            Assert.IsTrue(System.IO.Directory.Exists(path), $"Project root path should exist: {path}");
        }

        [Test]
        public void GetProjectRootPath_DoesNotContainAssets()
        {
            // Act
            string path = _launcher.GetProjectRootPath();

            // Assert
            Assert.IsFalse(path.EndsWith("Assets"), "Project root should not end with Assets");
        }

        #endregion

        #region CreateTerminalProcessStartInfo Tests

        [Test]
        public void CreateTerminalProcessStartInfo_EmptyCommand_ThrowsArgumentException()
        {
            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
            {
                _launcher.CreateTerminalProcessStartInfo(string.Empty);
            });
        }

        [Test]
        public void CreateTerminalProcessStartInfo_NullCommand_ThrowsArgumentException()
        {
            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
            {
                _launcher.CreateTerminalProcessStartInfo(null);
            });
        }

        [Test]
        public void CreateTerminalProcessStartInfo_WhitespaceCommand_ThrowsArgumentException()
        {
            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
            {
                _launcher.CreateTerminalProcessStartInfo("   ");
            });
        }

        [Test]
        public void CreateTerminalProcessStartInfo_ValidCommand_ReturnsStartInfo()
        {
            // Act
            var startInfo = _launcher.CreateTerminalProcessStartInfo("echo hello");

            // Assert
            Assert.IsNotNull(startInfo);
            Assert.IsNotNull(startInfo.FileName);
            Assert.IsNotEmpty(startInfo.FileName);
        }

        [Test]
        public void CreateTerminalProcessStartInfo_ValidCommand_SetsUseShellExecuteFalse()
        {
            // Act
            var startInfo = _launcher.CreateTerminalProcessStartInfo("echo hello");

            // Assert
            Assert.IsFalse(startInfo.UseShellExecute, "UseShellExecute should be false");
        }

        [Test]
        public void CreateTerminalProcessStartInfo_ValidCommand_SetsCreateNoWindowTrue()
        {
            // Act
            var startInfo = _launcher.CreateTerminalProcessStartInfo("echo hello");

            // Assert
            Assert.IsTrue(startInfo.CreateNoWindow, "CreateNoWindow should be true");
        }

        [Test]
        public void CreateTerminalProcessStartInfo_CommandWithNewlines_StripsNewlines()
        {
            // Act - Should not throw
            var startInfo = _launcher.CreateTerminalProcessStartInfo("echo\nhello\r\nworld");

            // Assert
            Assert.IsNotNull(startInfo);
        }

        [Test]
        public void CreateTerminalProcessStartInfo_LongCommand_HandlesGracefully()
        {
            // Arrange
            string longCommand = new string('a', 1000);

            // Act
            var startInfo = _launcher.CreateTerminalProcessStartInfo(longCommand);

            // Assert
            Assert.IsNotNull(startInfo);
        }

        [Test]
        public void CreateTerminalProcessStartInfo_SpecialCharacters_HandlesGracefully()
        {
            // Arrange
            string command = "echo \"hello world\" && echo 'test' | cat";

            // Act
            var startInfo = _launcher.CreateTerminalProcessStartInfo(command);

            // Assert
            Assert.IsNotNull(startInfo);
        }

        #endregion

        #region CreateHeadlessProcessStartInfo Tests

        [Test]
        public void CreateHeadlessProcessStartInfo_EmptyCommand_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                _launcher.CreateHeadlessProcessStartInfo(string.Empty, "/tmp/log.txt"));
        }

        [Test]
        public void CreateHeadlessProcessStartInfo_EmptyLogPath_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                _launcher.CreateHeadlessProcessStartInfo("echo hello", string.Empty));
        }

        [Test]
        public void CreateHeadlessProcessStartInfo_IsHiddenNoWindow()
        {
            var startInfo = _launcher.CreateHeadlessProcessStartInfo("echo hello", LogPath());

            Assert.IsFalse(startInfo.UseShellExecute, "UseShellExecute should be false for headless launch");
            Assert.IsTrue(startInfo.CreateNoWindow, "CreateNoWindow should be true for headless launch");
            Assert.AreEqual(System.Diagnostics.ProcessWindowStyle.Hidden, startInfo.WindowStyle,
                "WindowStyle should be Hidden for headless launch");
        }

        [Test]
        public void CreateHeadlessProcessStartInfo_DoesNotOpenTerminal()
        {
            var startInfo = _launcher.CreateHeadlessProcessStartInfo("echo hello", LogPath());

            Assert.AreEqual("echo", startInfo.FileName,
                "Headless launch should execute the requested binary directly");
            Assert.AreEqual("hello", startInfo.Arguments);
        }

        [Test]
        public void CreateHeadlessProcessStartInfo_RedirectsOutputToLogFile()
        {
            string logPath = LogPath();

            var startInfo = _launcher.CreateHeadlessProcessStartInfo("echo hello", logPath);

            Assert.IsTrue(startInfo.RedirectStandardOutput);
            Assert.IsTrue(startInfo.RedirectStandardError);
            StringAssert.DoesNotContain(logPath, startInfo.Arguments,
                "The log path must not be interpolated into a shell command");
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void CreateHeadlessProcessStartInfo_RedirectsStdinFromNul()
        {
            // Regression guard for #1279: the Editor is a console-less GUI process, so a child
            // launched with CreateNoWindow inherits an invalid stdin and uvx.exe fails with
            // "The handle is invalid. (os error 6)". stdin must come from NUL instead.
            var startInfo = _launcher.CreateHeadlessProcessStartInfo("uvx run-server", LogPath());

            Assert.IsTrue(startInfo.RedirectStandardInput,
                "stdin should use a managed redirected pipe rather than an inherited GUI handle");
        }
#endif

        [Test]
        public void CreateHeadlessProcessStartInfo_LogPathWithSpaces_IsQuoted()
        {
            // A log path containing spaces must remain a single token.
            string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Mcp Logs", "server launch.log");

            var startInfo = _launcher.CreateHeadlessProcessStartInfo("uvx run-server", logPath);

            Assert.IsTrue(System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(logPath)));
            StringAssert.DoesNotContain(logPath, startInfo.Arguments,
                "A log path must never become executable command text");
        }

        [Test]
        public void CreateHeadlessProcessStartInfo_CommandWithSpaces_Preserved()
        {
            string command = "\"/path with spaces/uvx\" --no-cache run mcp-for-unity";

            var startInfo = _launcher.CreateHeadlessProcessStartInfo(command, LogPath());

            Assert.AreEqual("/path with spaces/uvx", startInfo.FileName);
            Assert.AreEqual("--no-cache run mcp-for-unity", startInfo.Arguments);
        }

        [Test]
        public void CreateHeadlessProcessStartInfo_StripsNewlines()
        {
            var startInfo = _launcher.CreateHeadlessProcessStartInfo("echo\nhello\r\nworld", LogPath());

            Assert.IsNotNull(startInfo);
            StringAssert.DoesNotContain("\n", startInfo.Arguments);
            StringAssert.DoesNotContain("\r", startInfo.Arguments);
        }

        private static string LogPath()
        {
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-headless-test.log");
        }

        #endregion

        #region Interface Implementation Tests

        [Test]
        public void TerminalLauncher_ImplementsITerminalLauncher()
        {
            // Assert
            Assert.IsInstanceOf<ITerminalLauncher>(_launcher);
        }

        [Test]
        public void TerminalLauncher_CanBeUsedViaInterface()
        {
            // Arrange
            ITerminalLauncher launcher = new TerminalLauncher();

            // Act & Assert
            Assert.DoesNotThrow(() =>
            {
                launcher.GetProjectRootPath();
                launcher.CreateTerminalProcessStartInfo("test");
            });
        }

        #endregion

        #region Platform-Specific Behavior Tests

        [Test]
        public void CreateTerminalProcessStartInfo_ReturnsAppropriateTerminal()
        {
            // Act
            var startInfo = _launcher.CreateTerminalProcessStartInfo("echo test");

            // Assert - Platform-specific
#if UNITY_EDITOR_OSX
            Assert.AreEqual("/usr/bin/open", startInfo.FileName, "macOS should use 'open'");
#elif UNITY_EDITOR_WIN
            Assert.AreEqual("cmd.exe", startInfo.FileName, "Windows should use 'cmd.exe'");
#else
            // Linux uses detected terminal
            Assert.IsNotNull(startInfo.FileName, "Linux should have a terminal command");
#endif
        }

        #endregion
    }
}
