using System;
using System.Threading.Tasks;

namespace MCPForUnity.Editor.Services.Server
{
    public interface IServerRuntimeInstaller
    {
        bool EnsureInstalled(out InstalledServerRuntime runtime, out string error);
        Task<ServerRuntimeInstallResult> EnsureInstalledAsync(
            IProgress<ServerStartProgress> progress = null);
        bool TryGetInstalled(out InstalledServerRuntime runtime);
    }
}
