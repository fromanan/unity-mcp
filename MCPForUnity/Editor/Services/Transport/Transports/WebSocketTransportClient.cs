using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Transport.Transports
{
    /// <summary>
    /// Maintains a persistent WebSocket connection to the MCP server plugin hub.
    /// Handles registration, keep-alives, and command dispatch back into Unity via
    /// <see cref="TransportCommandDispatcher"/>.
    /// </summary>
    public class WebSocketTransportClient : IMcpTransportClient, IPersistentReconnectTransportClient, IDisposable
    {
        private const string TransportDisplayName = "websocket";
        private const int MaxIncomingMessageBytes = 16 * 1024 * 1024;
        private const int MaxOutgoingMessageBytes = 16 * 1024 * 1024;
        private static readonly TimeSpan[] ReconnectSchedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };
        private static readonly TimeSpan ReconnectTailInterval = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

        private enum DisconnectKind
        {
            ExpectedRemoteClose,
            RemoteClose,
            TransportFailure,
            HandshakeFailure,
            PlannedShutdown,
            PlannedReload
        }

        private sealed class DisconnectCause
        {
            public DisconnectCause(DisconnectKind kind, string reason)
            {
                Kind = kind;
                Reason = reason ?? "Connection closed";
            }

            public DisconnectKind Kind { get; }
            public string Reason { get; }
        }

        private sealed class ConnectionContext : IDisposable
        {
            private int _disconnectSignaled;

            public ConnectionContext(int generation, ClientWebSocket socket, CancellationToken lifecycleToken)
            {
                Generation = generation;
                ConnectionId = Guid.NewGuid().ToString("N");
                Socket = socket;
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifecycleToken);
                Ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Disconnected = new TaskCompletionSource<DisconnectCause>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public int Generation { get; }
            public string ConnectionId { get; }
            public ClientWebSocket Socket { get; }
            public CancellationTokenSource Cancellation { get; }
            public TaskCompletionSource<bool> Ready { get; }
            public TaskCompletionSource<DisconnectCause> Disconnected { get; }
            public Task ReceiveTask { get; set; }
            public Task EditorStateTask { get; set; }
            public string SessionId { get; set; }
            public bool SupportsEditorStatePush { get; set; }
            public bool SupportsReadyAcknowledgement { get; set; }
            public bool SupportsClientLifecycle { get; set; }

            public void SignalDisconnect(DisconnectKind kind, string reason)
            {
                if (Interlocked.CompareExchange(ref _disconnectSignaled, 1, 0) == 0)
                {
                    Disconnected.TrySetResult(new DisconnectCause(kind, reason));
                }
            }

            public void SignalDisconnect(string reason)
                => SignalDisconnect(DisconnectKind.TransportFailure, reason);

            public void Dispose()
            {
                Cancellation.Dispose();
                Socket.Dispose();
            }
        }

        private readonly IToolDiscoveryService _toolDiscoveryService;
        private CancellationTokenSource _lifecycleCts;
        private ConnectionContext _activeConnection;
        private Task _supervisorTask;
        private TaskCompletionSource<bool> _firstReady;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _editorStateSignal = new(0, 1);

        private Uri _endpointUri;
        private string _projectHash;
        private string _projectName;
        private string _projectPath;
        private string _unityVersion;
        private volatile bool _isConnected;
        private volatile TransportState _state = TransportState.Disconnected(TransportDisplayName, "Transport not started");
        private string _apiKey;
        private bool _editorStateSubscribed;
        private volatile bool _reloadDraining;
        private bool _hasReportedOutage;
        private volatile string _lastReconnectFailure;
        private int _connectionGeneration;
        private bool _disposed;

        public WebSocketTransportClient(IToolDiscoveryService toolDiscoveryService = null)
        {
            _toolDiscoveryService = toolDiscoveryService;
        }

        public bool IsConnected => _isConnected;
        public string TransportName => TransportDisplayName;
        public TransportState State => _state;
        public bool IsReconnectSupervisorActive =>
            _lifecycleCts != null
            && !_lifecycleCts.IsCancellationRequested
            && _supervisorTask != null
            && !_supervisorTask.IsCompleted;
        public string LastReconnectFailure => _lastReconnectFailure;

        private Task<List<ToolMetadata>> GetEnabledToolsOnMainThreadAsync(CancellationToken token)
        {
            return TransportCommandDispatcher.RunOnMainThreadAsync(
                () => _toolDiscoveryService?.GetEnabledTools() ?? new List<ToolMetadata>(),
                token);
        }

        public async Task<bool> StartAsync()
        {
            if (!await StartReconnectSupervisorAsync(replaceActiveSupervisor: true))
            {
                return false;
            }

            TaskCompletionSource<bool> firstReady = _firstReady;
            CancellationToken lifecycleToken = _lifecycleCts.Token;
            Task completed = await Task.WhenAny(
                firstReady.Task,
                Task.Delay(HandshakeTimeout, lifecycleToken)).ConfigureAwait(false);
            if (completed != firstReady.Task || !await firstReady.Task.ConfigureAwait(false))
            {
                string error = "Connection did not become ready before the handshake timeout";
                _lastReconnectFailure = error;
                await StopAsync();
                _state = TransportState.Disconnected(
                    TransportDisplayName,
                    error,
                    phase: TransportPhase.Faulted,
                    details: _endpointUri.ToString());
                return false;
            }
            return true;
        }

        public Task<bool> EnsureReconnectSupervisorAsync()
        {
            return StartReconnectSupervisorAsync(replaceActiveSupervisor: false);
        }

        private async Task<bool> StartReconnectSupervisorAsync(bool replaceActiveSupervisor)
        {
            if (!replaceActiveSupervisor && IsReconnectSupervisorActive)
            {
                return true;
            }

            // Capture identity values on the main thread before any async context switching
            _projectName = ProjectIdentityUtility.GetProjectName();
            _projectHash = ProjectIdentityUtility.GetProjectHash();
            _unityVersion = Application.unityVersion;
            _apiKey = HttpEndpointUtility.IsRemoteScope()
                ? EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty)
                : string.Empty;

            if (HttpEndpointUtility.IsRemoteScope()
                && !HttpEndpointUtility.IsCurrentRemoteUrlAllowed(out string remoteUrlError))
            {
                string message = remoteUrlError ?? "HTTP Remote URL is not allowed by current security settings.";
                _state = TransportState.Disconnected(TransportDisplayName, message);
                McpLog.Error($"[WebSocket] {message}");
                return false;
            }

            // Get project root path (strip /Assets from dataPath) for focus nudging
            string dataPath = Application.dataPath;
            _projectPath = null;
            if (!string.IsNullOrEmpty(dataPath))
            {
                string normalized = dataPath.TrimEnd('/', '\\');
                if (string.Equals(System.IO.Path.GetFileName(normalized), "Assets", StringComparison.Ordinal))
                {
                    _projectPath = System.IO.Path.GetDirectoryName(normalized) ?? normalized;
                }
                else
                {
                    _projectPath = normalized;  // Fallback if path doesn't end with Assets
                }
            }

            await StopAsync();

            _lifecycleCts = new CancellationTokenSource();
            _endpointUri = BuildWebSocketUri(HttpEndpointUtility.GetBaseUrl());
            _firstReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _reloadDraining = false;
            _hasReportedOutage = false;
            _lastReconnectFailure = null;
            SubscribeEditorState();

            _supervisorTask = Task.Run(
                () => SuperviseConnectionsAsync(_lifecycleCts.Token),
                CancellationToken.None);
            return true;
        }

        public async Task StopAsync()
        {
            UnsubscribeEditorState();

            if (_lifecycleCts == null)
            {
                return;
            }

            ConnectionContext connection = _activeConnection;
            _state = TransportState.Transitioning(
                TransportDisplayName,
                TransportPhase.Draining,
                details: _endpointUri?.ToString());
            if (connection != null)
            {
                await CloseConnectionAsync(connection, "Shutdown", graceful: true).ConfigureAwait(false);
            }

            try { _lifecycleCts.Cancel(); } catch { }
            if (_supervisorTask != null)
            {
                try { await _supervisorTask.ConfigureAwait(false); } catch { }
                _supervisorTask = null;
            }

            _isConnected = false;
            _state = TransportState.Disconnected(TransportDisplayName);

            _lifecycleCts.Dispose();
            _lifecycleCts = null;
        }

        /// <summary>
        /// Synchronous teardown for use in beforeAssemblyReload where async is not possible.
        /// Skips the graceful WebSocket close handshake and just disposes resources immediately.
        /// The server handles ungraceful disconnects through the WebSocket protocol timeout.
        /// </summary>
        public void ForceStop()
        {
            UnsubscribeEditorState();
            try { _lifecycleCts?.Cancel(); } catch { }
            ConnectionContext connection = Interlocked.Exchange(ref _activeConnection, null);
            if (connection != null)
            {
                try { connection.Cancellation.Cancel(); } catch { }
                connection.SignalDisconnect(DisconnectKind.PlannedShutdown, "Force stop");
                try { connection.Socket.Abort(); } catch { }
                try { connection.Dispose(); } catch { }
            }

            _supervisorTask = null;
            _isConnected = false;
            _state = TransportState.Disconnected(TransportDisplayName);

            try { _lifecycleCts?.Dispose(); } catch { }
            _lifecycleCts = null;
        }

        public async Task<bool> VerifyAsync()
        {
            ConnectionContext connection = _activeConnection;
            if (connection == null
                || connection.Socket.State != WebSocketState.Open
                || connection.Ready.Task.Status != TaskStatus.RanToCompletion
                || !connection.Ready.Task.Result)
            {
                return false;
            }

            if (_lifecycleCts == null)
            {
                return false;
            }

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                JObject snapshot = await TransportCommandDispatcher.RunOnMainThreadAsync(
                    EditorStateCache.GetSnapshot,
                    timeoutCts.Token).ConfigureAwait(false);
                return snapshot != null && ReferenceEquals(connection, _activeConnection);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Verify failed: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Ensure background loops are stopped before disposing shared resources
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Dispose failed to stop cleanly: {ex.Message}");
            }

            _sendLock?.Dispose();
            _editorStateSignal?.Dispose();
            _lifecycleCts?.Dispose();
            _disposed = true;
        }

        private async Task SuperviseConnectionsAsync(CancellationToken token)
        {
            int failedAttempts = 0;
            bool hasBeenReady = false;

            while (!token.IsCancellationRequested)
            {
                TimeSpan delay = GetReconnectDelay(failedAttempts);
                if (delay > TimeSpan.Zero)
                {
                    _state = TransportState.Transitioning(
                        TransportDisplayName,
                        TransportPhase.Backoff,
                        details: _endpointUri.ToString(),
                        error: $"Retrying in {delay.TotalSeconds:0.#} seconds");
                    try { await Task.Delay(delay, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }

                _state = TransportState.Transitioning(
                    TransportDisplayName,
                    TransportPhase.Connecting,
                    details: _endpointUri.ToString());
                ConnectionContext connection = null;
                string disconnectReason = "Connection failed";
                DisconnectKind disconnectKind = DisconnectKind.TransportFailure;
                try
                {
                    connection = await EstablishConnectionAsync(token).ConfigureAwait(false);
                    if (connection == null)
                    {
                        _lastReconnectFailure = string.IsNullOrWhiteSpace(_state?.Details)
                            ? _state?.Error
                            : _state.Details;
                        failedAttempts++;
                        continue;
                    }

                    _state = TransportState.Transitioning(
                        TransportDisplayName,
                        TransportPhase.Handshaking,
                        details: _endpointUri.ToString());
                    Task handshakeTimeout = Task.Delay(HandshakeTimeout, connection.Cancellation.Token);
                    Task completed = await Task.WhenAny(
                        connection.Ready.Task,
                        connection.Disconnected.Task,
                        handshakeTimeout).ConfigureAwait(false);
                    if (completed != connection.Ready.Task || !connection.Ready.Task.Result)
                    {
                        disconnectReason = completed == handshakeTimeout
                            ? "Registration handshake timed out"
                            : (await connection.Disconnected.Task.ConfigureAwait(false)).Reason;
                        _lastReconnectFailure = disconnectReason;
                        connection.SignalDisconnect(DisconnectKind.HandshakeFailure, disconnectReason);
                        failedAttempts++;
                        continue;
                    }

                    _isConnected = true;
                    _lastReconnectFailure = null;
                    hasBeenReady = true;
                    failedAttempts = 0;
                    _state = TransportState.Connected(
                        TransportDisplayName,
                        sessionId: connection.SessionId,
                        details: _endpointUri.ToString());
                    _firstReady?.TrySetResult(true);
                    if (_hasReportedOutage)
                    {
                        McpLog.Info("[WebSocket] Reconnected to MCP server", false);
                        _hasReportedOutage = false;
                    }

                    DisconnectCause cause = await connection.Disconnected.Task.ConfigureAwait(false);
                    disconnectReason = cause.Reason;
                    disconnectKind = cause.Kind;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    disconnectReason = ex.Message;
                    _lastReconnectFailure = disconnectReason;
                    failedAttempts++;
                    McpLog.Debug($"[WebSocket] Connection generation failed: {ex}");
                }
                finally
                {
                    _isConnected = false;
                    if (connection != null)
                    {
                        await CleanupConnectionAsync(connection).ConfigureAwait(false);
                    }
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (_reloadDraining)
                {
                    _state = TransportState.Transitioning(
                        TransportDisplayName,
                        TransportPhase.Draining,
                        details: _endpointUri.ToString());
                    try { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    break;
                }

                if (hasBeenReady
                    && !_hasReportedOutage
                    && ShouldWarnForDisconnect(disconnectKind))
                {
                    McpLog.Warn($"[WebSocket] Connection closed: {disconnectReason}");
                    _hasReportedOutage = true;
                }
            }

            _firstReady?.TrySetResult(false);
        }

        private async Task<ConnectionContext> EstablishConnectionAsync(CancellationToken token)
        {
            Uri originalEndpoint = _endpointUri;
            Exception lastConnectError = null;

            foreach (Uri candidate in BuildConnectionCandidateUris(originalEndpoint))
            {
                token.ThrowIfCancellationRequested();
                ClientWebSocket socket = new();
                ConnectionContext connection = null;
                socket.Options.KeepAliveInterval = TimeSpan.Zero;

                // Add API key header if configured (for remote-hosted mode)
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    socket.Options.SetRequestHeader(AuthConstants.ApiKeyHeader, _apiKey);
                }

                try
                {
                    await socket.ConnectAsync(candidate, token).ConfigureAwait(false);
                    if (!string.Equals(candidate.Host, originalEndpoint.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        McpLog.Warn($"[WebSocket] Connected via fallback host '{candidate.Host}' after '{originalEndpoint.Host}' failed.");
                        _endpointUri = candidate;
                    }

                    connection = new ConnectionContext(
                        Interlocked.Increment(ref _connectionGeneration),
                        socket,
                        token);
                    Interlocked.Exchange(ref _activeConnection, connection);
                    connection.ReceiveTask = Task.Run(
                        () => ReceiveLoopAsync(connection),
                        CancellationToken.None);
                    connection.EditorStateTask = Task.Run(
                        () => EditorStateLoopAsync(connection),
                        CancellationToken.None);
                    await SendRegisterAsync(connection, connection.Cancellation.Token).ConfigureAwait(false);
                    return connection;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (connection != null)
                    {
                        connection.SignalDisconnect(DisconnectKind.PlannedShutdown, "Connection cancelled");
                        await CleanupConnectionAsync(connection).ConfigureAwait(false);
                    }
                    else
                    {
                        socket.Dispose();
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    lastConnectError = ex;
                    if (connection != null)
                    {
                        connection.SignalDisconnect(ex.Message);
                        await CleanupConnectionAsync(connection).ConfigureAwait(false);
                    }
                    else
                    {
                        socket.Dispose();
                    }
                    McpLog.Debug($"[WebSocket] Connect failed for {candidate}: {ex.Message}");
                }
            }

            _state = TransportState.Disconnected(
                TransportDisplayName,
                "Connection failed. Check the server URL, server process, and API key.",
                phase: TransportPhase.Backoff,
                details: lastConnectError?.Message);
            return null;
        }

        private async Task CleanupConnectionAsync(ConnectionContext connection)
        {
            Interlocked.CompareExchange(ref _activeConnection, null, connection);
            try { connection.Cancellation.Cancel(); } catch { }
            if (connection.ReceiveTask != null)
            {
                try { await connection.ReceiveTask.ConfigureAwait(false); } catch { }
            }
            if (connection.EditorStateTask != null)
            {
                try { await connection.EditorStateTask.ConfigureAwait(false); } catch { }
            }
            connection.Dispose();
        }

        private async Task CloseConnectionAsync(
            ConnectionContext connection,
            string reason,
            bool graceful)
        {
            if (graceful
                && (connection.Socket.State == WebSocketState.Open
                    || connection.Socket.State == WebSocketState.CloseReceived))
            {
                try
                {
                    using CancellationTokenSource closeTimeout = new(TimeSpan.FromSeconds(1));
                    await connection.Socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        reason,
                        closeTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    McpLog.Debug($"[WebSocket] Graceful close did not complete: {ex.Message}");
                    try { connection.Socket.Abort(); } catch { }
                }
            }
            else
            {
                try { connection.Socket.Abort(); } catch { }
            }

            DisconnectKind disconnectKind = reason == "Unity assembly reload"
                ? DisconnectKind.PlannedReload
                : DisconnectKind.PlannedShutdown;
            connection.SignalDisconnect(disconnectKind, reason);
            try { connection.Cancellation.Cancel(); } catch { }
        }

        private static TimeSpan GetReconnectDelay(int failedAttempts)
        {
            if (failedAttempts < ReconnectSchedule.Length)
            {
                return ReconnectSchedule[failedAttempts];
            }
            return ReconnectTailInterval;
        }

        private async Task ReceiveLoopAsync(ConnectionContext connection)
        {
            CancellationToken token = connection.Cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string message = await ReceiveMessageAsync(connection, token).ConfigureAwait(false);
                    if (message == null)
                    {
                        break;
                    }
                    await HandleMessageAsync(connection, message, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException wse)
                {
                    McpLog.Debug($"[WebSocket] Receive loop error: {wse.Message}");
                    connection.SignalDisconnect(wse.Message);
                    break;
                }
                catch (Exception ex)
                {
                    McpLog.Debug($"[WebSocket] Unexpected receive error: {ex.Message}");
                    connection.SignalDisconnect(ex.Message);
                    break;
                }
            }
        }

        private static async Task<string> ReceiveMessageAsync(
            ConnectionContext connection,
            CancellationToken token)
        {
            byte[] rentedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
            ArraySegment<byte> buffer = new(rentedBuffer);
            using MemoryStream ms = new(8192);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await connection.Socket.ReceiveAsync(buffer, token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        WebSocketCloseStatus? closeStatus = result.CloseStatus;
                        string closeReason = string.IsNullOrWhiteSpace(result.CloseStatusDescription)
                            ? closeStatus.HasValue
                                ? $"Server closed connection ({(int)closeStatus.Value} {closeStatus.Value})"
                                : "Server closed connection without a close status"
                            : result.CloseStatusDescription;
                        connection.SignalDisconnect(
                            IsExpectedRemoteClose(closeStatus)
                                ? DisconnectKind.ExpectedRemoteClose
                                : DisconnectKind.RemoteClose,
                            closeReason);
                        return null;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await connection.Socket.CloseOutputAsync(
                            WebSocketCloseStatus.InvalidMessageType,
                            "Only text messages are supported",
                            token).ConfigureAwait(false);
                        throw new InvalidDataException("Received a non-text WebSocket message");
                    }

                    if (result.Count > 0)
                    {
                        if (ms.Length + result.Count > MaxIncomingMessageBytes)
                        {
                            await connection.Socket.CloseOutputAsync(
                                WebSocketCloseStatus.MessageTooBig,
                                "Message exceeded server limit",
                                token).ConfigureAwait(false);
                            throw new InvalidDataException(
                                $"WebSocket message exceeded {MaxIncomingMessageBytes} bytes");
                        }
                        ms.Write(buffer.Array!, buffer.Offset, result.Count);
                    }

                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                if (ms.Length == 0)
                {
                    return null;
                }

                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, checked((int)ms.Length));
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        private async Task HandleMessageAsync(
            ConnectionContext connection,
            string message,
            CancellationToken token)
        {
            JObject payload;
            try
            {
                payload = JObject.Parse(message);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[WebSocket] Invalid JSON payload: {ex.Message}");
                return;
            }

            string messageType = payload.Value<string>("type") ?? string.Empty;

            switch (messageType)
            {
                case "welcome":
                    ApplyWelcome(connection, payload);
                    break;
                case "registered":
                    await HandleRegisteredAsync(connection, payload, token).ConfigureAwait(false);
                    break;
                case "plugin_ready_ack":
                    HandleReadyAcknowledgement(connection, payload);
                    break;
                case "execute":
                    await HandleExecuteAsync(connection, payload, token).ConfigureAwait(false);
                    break;
                default:
                    // No-op for unrecognised types (keep-alives, telemetry, etc.)
                    break;
            }
        }

        private void ApplyWelcome(ConnectionContext connection, JObject payload)
        {
            JArray capabilities = payload["capabilities"] as JArray;
            connection.SupportsEditorStatePush = false;
            connection.SupportsReadyAcknowledgement = false;
            connection.SupportsClientLifecycle = false;
            if (capabilities != null)
            {
                foreach (JToken capability in capabilities)
                {
                    if (string.Equals(capability?.Value<string>(), "editor_state_push_v1",
                            StringComparison.Ordinal))
                    {
                        connection.SupportsEditorStatePush = true;
                    }
                    else if (string.Equals(capability?.Value<string>(), "plugin_ready_ack_v1",
                                 StringComparison.Ordinal))
                    {
                        connection.SupportsReadyAcknowledgement = true;
                    }
                    else if (string.Equals(capability?.Value<string>(), "client_lifecycle_v1",
                                 StringComparison.Ordinal))
                    {
                        connection.SupportsClientLifecycle = true;
                    }
                }
            }

        }

        private async Task HandleRegisteredAsync(
            ConnectionContext connection,
            JObject payload,
            CancellationToken token)
        {
            string newSessionId = payload.Value<string>("session_id");
            string responseConnectionId = payload.Value<string>("connection_id");
            if (string.IsNullOrEmpty(newSessionId))
            {
                throw new InvalidDataException("Registered response did not include a session ID");
            }
            if (!string.IsNullOrEmpty(responseConnectionId)
                && !string.Equals(responseConnectionId, connection.ConnectionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Registered response targeted a stale connection generation");
            }

            connection.SessionId = newSessionId;
            ProjectIdentityUtility.SetSessionId(newSessionId);
            await SendRegisterToolsAsync(connection, token).ConfigureAwait(false);

            bool readyRequired = payload.Value<bool?>("ready_required") ?? false;
            if (connection.SupportsReadyAcknowledgement && readyRequired)
            {
                await SendPluginReadyAsync(connection, token).ConfigureAwait(false);
                return;
            }

            connection.Ready.TrySetResult(true);
            await SendEditorStateAsync(connection, token).ConfigureAwait(false);
        }

        private void HandleReadyAcknowledgement(
            ConnectionContext connection,
            JObject payload)
        {
            string sessionId = payload.Value<string>("session_id");
            string connectionId = payload.Value<string>("connection_id");
            if (!string.Equals(sessionId, connection.SessionId, StringComparison.Ordinal)
                || !string.Equals(connectionId, connection.ConnectionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Ready acknowledgement targeted a stale connection generation");
            }

            connection.Ready.TrySetResult(true);
            _ = SendEditorStateAfterReadyAsync(connection);
        }

        private async Task SendEditorStateAfterReadyAsync(ConnectionContext connection)
        {
            try
            {
                await SendEditorStateAsync(connection, connection.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                connection.SignalDisconnect(ex.Message);
            }
        }

        private void SubscribeEditorState()
        {
            if (_editorStateSubscribed)
            {
                return;
            }

            EditorStateCache.SnapshotChanged += OnEditorStateSnapshotChanged;
            _editorStateSubscribed = true;
        }

        private void UnsubscribeEditorState()
        {
            if (!_editorStateSubscribed)
            {
                return;
            }

            EditorStateCache.SnapshotChanged -= OnEditorStateSnapshotChanged;
            _editorStateSubscribed = false;
        }

        private void OnEditorStateSnapshotChanged()
        {
            if (!_editorStateSubscribed || _editorStateSignal.CurrentCount != 0)
            {
                return;
            }

            try
            {
                _editorStateSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another change already queued a coalesced snapshot push.
            }
        }

        private async Task EditorStateLoopAsync(ConnectionContext connection)
        {
            CancellationToken token = connection.Cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool stateChanged = await _editorStateSignal.WaitAsync(
                        TimeSpan.FromSeconds(1), token).ConfigureAwait(false);

                    if (stateChanged)
                    {
                        while (_editorStateSignal.Wait(0))
                        {
                            // Coalesce every pending state change into one current snapshot.
                        }

                        await SendEditorStateAsync(connection, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendEditorHeartbeatAsync(connection, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    McpLog.Debug($"[WebSocket] Editor state loop stopped: {ex.Message}");
                    connection.SignalDisconnect(ex.Message);
                    break;
                }
            }
        }

        private async Task SendEditorStateAsync(
            ConnectionContext connection,
            CancellationToken token)
        {
            if (!connection.SupportsEditorStatePush
                || string.IsNullOrEmpty(connection.SessionId)
                || !connection.Ready.Task.IsCompleted)
            {
                return;
            }

            JObject snapshot = await TransportCommandDispatcher.RunOnMainThreadAsync(
                EditorStateCache.GetSnapshot, token).ConfigureAwait(false);
            JObject payload = new()
            {
                ["type"] = "editor_state",
                ["session_id"] = connection.SessionId,
                ["state"] = snapshot
            };

            await SendJsonAsync(connection, payload, token).ConfigureAwait(false);
        }

        private Task SendEditorHeartbeatAsync(
            ConnectionContext connection,
            CancellationToken token)
        {
            if (!connection.SupportsEditorStatePush
                || string.IsNullOrEmpty(connection.SessionId)
                || !connection.Ready.Task.IsCompleted)
            {
                return Task.CompletedTask;
            }

            JObject payload = new()
            {
                ["type"] = "editor_heartbeat",
                ["session_id"] = connection.SessionId,
                ["editor_heartbeat_unix_ms"] = EditorStateCache.GetLastMainThreadHeartbeatUnixMs()
            };

            return SendJsonAsync(connection, payload, token);
        }

        private async Task SendRegisterToolsAsync(
            ConnectionContext connection,
            CancellationToken token)
        {
            if (_toolDiscoveryService == null) return;

            token.ThrowIfCancellationRequested();
            var tools = await GetEnabledToolsOnMainThreadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            McpLog.Info($"[WebSocket] Preparing to register {tools.Count} tool(s) with the bridge.", false);
            var toolsArray = new JArray();

            foreach (var tool in tools)
            {
                var toolObj = new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["structured_output"] = tool.StructuredOutput,
                    ["requires_polling"] = tool.RequiresPolling,
                    ["poll_action"] = tool.PollAction ?? "status",
                    ["max_poll_seconds"] = tool.MaxPollSeconds,
                    ["group"] = string.IsNullOrWhiteSpace(tool.Group) ? "core" : tool.Group,
                    ["is_built_in"] = tool.IsBuiltIn
                };

                var paramsArray = new JArray();
                if (tool.Parameters != null)
                {
                    foreach (var p in tool.Parameters)
                    {
                        paramsArray.Add(new JObject
                        {
                            ["name"] = p.Name,
                            ["description"] = p.Description,
                            ["type"] = p.Type,
                            ["required"] = p.Required,
                            ["default_value"] = p.DefaultValue
                        });
                    }
                }
                toolObj["parameters"] = paramsArray;
                toolsArray.Add(toolObj);
            }

            var payload = new JObject
            {
                ["type"] = "register_tools",
                ["tools"] = toolsArray
            };

            await SendJsonAsync(connection, payload, token).ConfigureAwait(false);
            McpLog.Info($"[WebSocket] Sent {tools.Count} tools registration", false);
        }

        public async Task ReregisterToolsAsync()
        {
            ConnectionContext connection = _activeConnection;
            if (!IsConnected || _lifecycleCts == null || connection == null)
            {
                McpLog.Warn("[WebSocket] Cannot reregister tools: not connected");
                return;
            }

            try
            {
                await SendRegisterToolsAsync(connection, _lifecycleCts.Token).ConfigureAwait(false);
                McpLog.Info("[WebSocket] Tool reregistration completed", false);
            }
            catch (System.OperationCanceledException)
            {
                McpLog.Warn("[WebSocket] Tool reregistration cancelled");
            }
            catch (System.Exception ex)
            {
                McpLog.Error($"[WebSocket] Tool reregistration failed: {ex.Message}");
            }
        }

        private async Task HandleExecuteAsync(
            ConnectionContext connection,
            JObject payload,
            CancellationToken token)
        {
            string commandId = payload.Value<string>("id");
            string commandName = payload.Value<string>("name");
            JObject parameters = payload.Value<JObject>("params") ?? new JObject();
            int timeoutSeconds = payload.Value<int?>("timeout") ?? (int)DefaultCommandTimeout.TotalSeconds;

            if (string.IsNullOrEmpty(commandId) || string.IsNullOrEmpty(commandName))
            {
                McpLog.Warn("[WebSocket] Invalid execute payload (missing id or name)");
                return;
            }

            if (_reloadDraining)
            {
                JObject retryResponse = new()
                {
                    ["type"] = "command_result",
                    ["id"] = commandId,
                    ["result"] = new JObject
                    {
                        ["success"] = false,
                        ["error"] = "Unity is compiling or reloading; please retry",
                        ["data"] = new JObject
                        {
                            ["reason"] = "unity_reloading",
                            ["retry_after_ms"] = 250
                        }
                    }
                };
                await SendJsonAsync(connection, retryResponse, token).ConfigureAwait(false);
                return;
            }

            var commandEnvelope = new JObject
            {
                ["type"] = commandName,
                ["params"] = parameters
            };

            string responseJson;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
                responseJson = await TransportCommandDispatcher.ExecuteCommandJsonAsync(commandEnvelope.ToString(Formatting.None), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = $"Command '{commandName}' timed out after {timeoutSeconds} seconds"
                });
            }
            catch (Exception ex)
            {
                responseJson = JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = ex.Message
                });
            }

            JToken resultToken;
            int responseBytes = responseJson == null
                ? 0
                : Encoding.UTF8.GetByteCount(responseJson);
            if (responseBytes > MaxOutgoingMessageBytes)
            {
                resultToken = new JObject
                {
                    ["success"] = false,
                    ["status"] = "error",
                    ["error"] = "Command result exceeded the WebSocket message limit",
                    ["data"] = new JObject
                    {
                        ["resultBytes"] = responseBytes,
                        ["maxResultBytes"] = MaxOutgoingMessageBytes,
                    },
                };
            }
            else
            {
                try
                {
                    resultToken = JToken.Parse(responseJson);
                }
                catch
                {
                    resultToken = new JObject
                    {
                        ["status"] = "error",
                        ["error"] = "Invalid response payload"
                    };
                }
            }

            var responsePayload = new JObject
            {
                ["type"] = "command_result",
                ["id"] = commandId,
                ["result"] = resultToken
            };

            await SendJsonAsync(connection, responsePayload, token).ConfigureAwait(false);
        }

        private async Task SendRegisterAsync(
            ConnectionContext connection,
            CancellationToken token)
        {
            var registerPayload = new JObject
            {
                ["type"] = "register",
                // session_id is now server-authoritative; omitted here or sent as null
                ["project_name"] = _projectName,
                ["project_hash"] = _projectHash,
                ["unity_version"] = _unityVersion,
                ["project_path"] = _projectPath,
                ["connection_id"] = connection.ConnectionId,
                ["capabilities"] = new JArray(
                    "plugin_ready_ack_v1",
                    "client_lifecycle_v1")
            };

            await SendJsonAsync(connection, registerPayload, token).ConfigureAwait(false);
        }

        private Task SendPluginReadyAsync(
            ConnectionContext connection,
            CancellationToken token)
        {
            JObject payload = new()
            {
                ["type"] = "plugin_ready",
                ["session_id"] = connection.SessionId,
                ["connection_id"] = connection.ConnectionId
            };
            return SendJsonAsync(connection, payload, token);
        }

        private async Task SendJsonAsync(
            ConnectionContext connection,
            JObject payload,
            CancellationToken token)
        {
            if (connection == null)
            {
                throw new InvalidOperationException("WebSocket is not initialised");
            }

            string json = payload.ToString(Formatting.None);
            int byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > MaxOutgoingMessageBytes)
            {
                throw new InvalidDataException(
                    $"WebSocket message exceeded {MaxOutgoingMessageBytes} bytes");
            }
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var buffer = new ArraySegment<byte>(bytes);

            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(connection, _activeConnection))
                {
                    throw new InvalidOperationException("WebSocket connection generation is stale");
                }
                if (connection.Socket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("WebSocket is not open");
                }

                await connection.Socket.SendAsync(buffer, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Publishes a compilation or reload lifecycle transition to the active server.
        /// </summary>
        public async Task NotifyLifecycleAsync(string lifecycleState)
        {
            ConnectionContext connection = _activeConnection;
            if (connection == null || string.IsNullOrWhiteSpace(lifecycleState))
            {
                return;
            }

            string normalizedState = lifecycleState.Trim().ToLowerInvariant();
            bool isDraining = normalizedState == "compiling"
                || normalizedState == "reloading"
                || normalizedState == "draining";
            _reloadDraining = isDraining;
            if (isDraining)
            {
                _isConnected = false;
                _state = TransportState.Transitioning(
                    TransportDisplayName,
                    TransportPhase.Draining,
                    details: _endpointUri?.ToString());
            }
            else if (normalizedState == "ready" && connection.Ready.Task.IsCompleted)
            {
                _isConnected = true;
                _state = TransportState.Connected(
                    TransportDisplayName,
                    sessionId: connection.SessionId,
                    details: _endpointUri?.ToString());
            }

            if (connection.SupportsClientLifecycle
                && connection.Socket.State == WebSocketState.Open)
            {
                JObject payload = new()
                {
                    ["type"] = "client_lifecycle",
                    ["state"] = normalizedState,
                    ["session_id"] = connection.SessionId,
                    ["connection_id"] = connection.ConnectionId
                };
                await SendJsonAsync(connection, payload, connection.Cancellation.Token).ConfigureAwait(false);
            }

            if (normalizedState == "reloading")
            {
                await CloseConnectionAsync(connection, "Unity assembly reload", graceful: true).ConfigureAwait(false);
            }
        }

        private static Uri BuildWebSocketUri(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var httpUri))
            {
                throw new InvalidOperationException($"Invalid MCP base URL: {baseUrl}");
            }

            // Replace bind-only addresses for client connections
            // 0.0.0.0 and :: are only valid for server binding, not client connections
            string host = httpUri.Host;
            if (host == "0.0.0.0")
            {
                McpLog.Warn($"[WebSocket] Base URL host '{host}' is bind-only; using '127.0.0.1' for client connection.");
                host = "127.0.0.1";
            }
            else if (host == "::")
            {
                McpLog.Warn($"[WebSocket] Base URL host '{host}' is bind-only; using '::1' for client connection.");
                host = "::1";
            }

            var builder = new UriBuilder(httpUri)
            {
                Scheme = httpUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                Host = host,
                Path = httpUri.AbsolutePath.TrimEnd('/') + "/hub/plugin"
            };

            return builder.Uri;
        }

        private static bool IsExpectedRemoteClose(WebSocketCloseStatus? closeStatus)
            => closeStatus == WebSocketCloseStatus.NormalClosure
                || closeStatus == WebSocketCloseStatus.EndpointUnavailable;

        private static bool ShouldWarnForDisconnect(DisconnectKind disconnectKind)
            => disconnectKind != DisconnectKind.ExpectedRemoteClose
                && disconnectKind != DisconnectKind.PlannedReload
                && disconnectKind != DisconnectKind.PlannedShutdown;

        private static List<Uri> BuildConnectionCandidateUris(Uri endpointUri)
        {
            var candidates = new List<Uri>();
            if (endpointUri == null)
            {
                return candidates;
            }

            candidates.Add(endpointUri);

            if (!string.Equals(endpointUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return candidates;
            }

            // Retry localhost using explicit loopback hosts to avoid DNS family ambiguity on some machines.
            TryAddCandidate(candidates, endpointUri, "127.0.0.1");
            TryAddCandidate(candidates, endpointUri, "::1");
            return candidates;
        }

        private static void TryAddCandidate(List<Uri> candidates, Uri template, string host)
        {
            try
            {
                var builder = new UriBuilder(template) { Host = host };
                Uri candidate = builder.Uri;
                foreach (Uri existing in candidates)
                {
                    if (Uri.Compare(existing, candidate, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        return;
                    }
                }
                candidates.Add(candidate);
            }
            catch
            {
                // Ignore malformed fallback candidate and continue with remaining options.
            }
        }
    }
}
