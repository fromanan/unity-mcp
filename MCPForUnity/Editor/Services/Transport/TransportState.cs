namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Identifies the current transport lifecycle phase.
    /// </summary>
    public enum TransportPhase
    {
        Stopped,
        Connecting,
        Handshaking,
        Ready,
        Backoff,
        Draining,
        Faulted
    }

    /// <summary>
    /// Lightweight snapshot of a transport's runtime status for editor UI and diagnostics.
    /// </summary>
    public sealed class TransportState
    {
        public bool IsConnected { get; }
        public string TransportName { get; }
        public int? Port { get; }
        public string SessionId { get; }
        public string Details { get; }
        public string Error { get; }
        public TransportPhase Phase { get; }

        private TransportState(
            bool isConnected,
            string transportName,
            int? port,
            string sessionId,
            string details,
            string error,
            TransportPhase phase)
        {
            IsConnected = isConnected && phase == TransportPhase.Ready;
            TransportName = transportName;
            Port = port;
            SessionId = sessionId;
            Details = details;
            Error = error;
            Phase = phase;
        }

        public static TransportState Connected(
            string transportName,
            int? port = null,
            string sessionId = null,
            string details = null)
            => new TransportState(true, transportName, port, sessionId, details, null, TransportPhase.Ready);

        public static TransportState Disconnected(
            string transportName,
            string error = null,
            int? port = null,
            TransportPhase phase = TransportPhase.Stopped,
            string details = null)
            => new TransportState(false, transportName, port, null, details, error, phase);

        public static TransportState Transitioning(
            string transportName,
            TransportPhase phase,
            string details = null,
            string error = null,
            int? port = null)
            => new TransportState(
                false,
                transportName,
                port,
                null,
                details,
                error,
                phase);
    }
}
