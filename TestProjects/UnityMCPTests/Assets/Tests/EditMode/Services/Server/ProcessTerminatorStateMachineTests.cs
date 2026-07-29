using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Services.Server;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services.Server
{
    [TestFixture]
    public class ProcessTerminatorStateMachineTests
    {
        private sealed class FakeDetector : IProcessDetector
        {
            public bool Exists = true;
            public bool PortListening = true;

            public bool LooksLikeMcpServerProcess(int pid) => true;
            public bool TryGetProcessCommandLine(int pid, out string argsLower)
            {
                argsLower = "mcp-for-unity";
                return true;
            }
            public List<int> GetListeningProcessIdsForPort(int port) =>
                PortListening ? new List<int> { 42 } : new List<int>();
            public int GetCurrentProcessId() => 999;
            public bool ProcessExists(int pid) => Exists;
            public string NormalizeForMatch(string input) => input;
        }

        private sealed class FakeRunner : IProcessCommandRunner
        {
            public int Calls;
            public Action<string> OnRun;

            public bool Run(string fileName, string arguments, out string stdout, out string stderr)
            {
                Calls++;
                OnRun?.Invoke(arguments);
                stdout = string.Empty;
                stderr = string.Empty;
                return true;
            }
        }

        [Test]
        public void Terminate_GracefulExit_VerifiesProcessAndPort()
        {
            var detector = new FakeDetector();
            var runner = new FakeRunner
            {
                OnRun = _ =>
                {
                    detector.Exists = false;
                    detector.PortListening = false;
                }
            };
            DateTime now = DateTime.UtcNow;
            var terminator = new ProcessTerminator(
                detector,
                runner,
                milliseconds => now = now.AddMilliseconds(milliseconds),
                () => now);

            Assert.IsTrue(terminator.Terminate(42, 8080));
            Assert.AreEqual(1, runner.Calls);
        }

        [Test]
        public void Terminate_CommandSuccessWithoutExit_EscalatesToForce()
        {
            var detector = new FakeDetector();
            var runner = new FakeRunner
            {
                OnRun = arguments =>
                {
                    if (!arguments.Contains("/F")) return;
                    detector.Exists = false;
                    detector.PortListening = false;
                }
            };
            DateTime now = DateTime.UtcNow;
            var terminator = new ProcessTerminator(
                detector,
                runner,
                milliseconds => now = now.AddMilliseconds(milliseconds),
                () => now);

            Assert.IsTrue(terminator.Terminate(42, 8080));
            Assert.AreEqual(2, runner.Calls);
        }

        [Test]
        public void Terminate_ProcessOrPortStillPresent_ReturnsFalse()
        {
            var detector = new FakeDetector();
            var runner = new FakeRunner();
            DateTime now = DateTime.UtcNow;
            var terminator = new ProcessTerminator(
                detector,
                runner,
                milliseconds => now = now.AddMilliseconds(milliseconds),
                () => now);

            Assert.IsFalse(terminator.Terminate(42, 8080));
            Assert.AreEqual(2, runner.Calls);
        }
    }
}
