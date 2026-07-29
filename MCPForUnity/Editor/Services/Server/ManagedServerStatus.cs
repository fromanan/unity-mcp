namespace MCPForUnity.Editor.Services.Server
{
    public sealed class ManagedServerStatus
    {
        public int SupervisorPid { get; internal set; }
        public int ServerPid { get; internal set; }
        public int UnityPid { get; internal set; }
        public int Port { get; internal set; }
        public int ActiveProcesses { get; internal set; }
        public long CurrentPrivateBytes { get; internal set; }
        public long PeakJobMemoryBytes { get; internal set; }
        public long SoftMemoryLimitBytes { get; internal set; }
        public long HardMemoryLimitBytes { get; internal set; }
        public string RuntimeVersion { get; internal set; }
        public double LaunchedAtUnix { get; internal set; }
        public int? ActiveHttpSessions { get; internal set; }
        public int? MaximumHttpSessions { get; internal set; }
        public string ExitReason { get; internal set; }
        public int? ServerExitCode { get; internal set; }
    }
}
