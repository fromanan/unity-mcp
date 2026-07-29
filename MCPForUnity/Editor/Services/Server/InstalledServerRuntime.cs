namespace MCPForUnity.Editor.Services.Server
{
    public sealed class InstalledServerRuntime
    {
        public InstalledServerRuntime(
            string rootPath,
            string serverExecutable,
            string supervisorExecutable,
            string manifestPath,
            string version,
            string source)
        {
            RootPath = rootPath;
            ServerExecutable = serverExecutable;
            SupervisorExecutable = supervisorExecutable;
            ManifestPath = manifestPath;
            Version = version;
            Source = source;
        }

        public string RootPath { get; }
        public string ServerExecutable { get; }
        public string SupervisorExecutable { get; }
        public string ManifestPath { get; }
        public string Version { get; }
        public string Source { get; }
    }
}
