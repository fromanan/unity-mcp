using System;

namespace MCPForUnity.Editor.Services.Server
{
    public readonly struct ServerStartProgress
    {
        public ServerStartProgress(float normalizedProgress, string message)
        {
            NormalizedProgress = Math.Max(0f, Math.Min(1f, normalizedProgress));
            Message = message ?? string.Empty;
        }

        public float NormalizedProgress { get; }
        public string Message { get; }
    }

    public sealed class ServerRuntimeInstallResult
    {
        private ServerRuntimeInstallResult(
            bool success,
            InstalledServerRuntime runtime,
            string error)
        {
            Success = success;
            Runtime = runtime;
            Error = error;
        }

        public bool Success { get; }
        public InstalledServerRuntime Runtime { get; }
        public string Error { get; }

        public static ServerRuntimeInstallResult Succeeded(InstalledServerRuntime runtime)
        {
            return new ServerRuntimeInstallResult(true, runtime, null);
        }

        public static ServerRuntimeInstallResult Failed(string error)
        {
            return new ServerRuntimeInstallResult(false, null, error);
        }
    }
}
