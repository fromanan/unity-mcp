namespace MCPForUnity.Editor.Services.Server
{
    public interface IServerRuntimeInstaller
    {
        bool EnsureInstalled(out InstalledServerRuntime runtime, out string error);
        bool TryGetInstalled(out InstalledServerRuntime runtime);
    }
}
