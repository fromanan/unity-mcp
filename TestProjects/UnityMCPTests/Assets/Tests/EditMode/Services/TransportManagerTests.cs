using System.Threading.Tasks;
using NUnit.Framework;
using MCPForUnity.Editor.Services.Transport;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Pins TransportManager.StartAsync's coalescing contract: concurrent starts for the
    /// same mode share one in-flight attempt instead of racing — a second StartAsync would
    /// otherwise tear down the first connection mid-handshake (manual Connect vs the
    /// reload-resume/auto-start loops).
    /// </summary>
    public class TransportManagerTests
    {
        private sealed class PendingTransportClient : IMcpTransportClient
        {
            public readonly TaskCompletionSource<bool> Pending = new TaskCompletionSource<bool>();
            public int StartCalls;

            public bool IsConnected => false;
            public string TransportName => "http";
            public TransportState State { get; } = TransportState.Disconnected("http");

            public Task<bool> StartAsync()
            {
                StartCalls++;
                return Pending.Task;
            }

            public Task StopAsync() => Task.CompletedTask;
            public Task<bool> VerifyAsync() => Task.FromResult(false);
            public Task ReregisterToolsAsync() => Task.CompletedTask;
        }

        private sealed class MutableStateTransportClient : IMcpTransportClient
        {
            public bool IsConnected => State.IsConnected;
            public string TransportName => "http";
            public TransportState State { get; set; } = TransportState.Connected("http");

            public Task<bool> StartAsync() => Task.FromResult(true);
            public Task StopAsync() => Task.CompletedTask;
            public Task<bool> VerifyAsync() => Task.FromResult(IsConnected);
            public Task ReregisterToolsAsync() => Task.CompletedTask;
        }

        [Test]
        public void StartAsync_ConcurrentCallsSameMode_CoalesceIntoOneAttempt()
        {
            var client = new PendingTransportClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);

            Task<bool> first = manager.StartAsync(TransportMode.Http);
            Task<bool> second = manager.StartAsync(TransportMode.Http);

            Assert.AreEqual(1, client.StartCalls, "concurrent starts must share one client attempt");
            Assert.AreSame(first, second, "the in-flight task is returned to concurrent callers");

            client.Pending.SetResult(true); // let the shared attempt finish
        }

        [Test]
        public void StartAsync_AfterCompletedStart_StartsFresh()
        {
            var client = new FakeTransportClient();
            var manager = new TransportManager();
            manager.Configure(() => client, () => client);

            Task<bool> first = manager.StartAsync(TransportMode.Http);
            Assert.IsTrue(first.IsCompleted && first.Result, "fake start should complete synchronously");

            Task<bool> second = manager.StartAsync(TransportMode.Http);
            Assert.AreEqual(2, client.StartCalls, "a completed start must not block later restarts");
            Assert.IsTrue(second.IsCompleted && second.Result);
        }

        [Test]
        public void GetState_UsesLiveClientStateAfterInternalDisconnect()
        {
            MutableStateTransportClient client = new();
            TransportManager manager = new();
            manager.Configure(() => client, () => client);

            Assert.IsTrue(manager.StartAsync(TransportMode.Http).Result);
            client.State = TransportState.Disconnected(
                "http",
                "socket closed",
                phase: TransportPhase.Backoff);

            Assert.IsFalse(manager.IsRunning(TransportMode.Http));
            Assert.AreEqual(TransportPhase.Backoff, manager.GetState(TransportMode.Http).Phase);
            Assert.AreEqual("socket closed", manager.GetState(TransportMode.Http).Error);
        }

        [Test]
        public void TransitioningState_NeverReportsConnected()
        {
            TransportState state = TransportState.Transitioning(
                "http",
                TransportPhase.Handshaking,
                details: "waiting for ready acknowledgement");

            Assert.IsFalse(state.IsConnected);
            Assert.AreEqual(TransportPhase.Handshaking, state.Phase);
        }
    }
}
