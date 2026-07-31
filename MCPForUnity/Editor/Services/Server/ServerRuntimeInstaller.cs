using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>
    /// Installs the Python server into a versioned project-local virtual
    /// environment. uv exits after installation and is never the server parent.
    /// </summary>
    public sealed class ServerRuntimeInstaller : IServerRuntimeInstaller
    {
        private static readonly SemaphoreSlim InstallGate = new SemaphoreSlim(1, 1);
        private const int InstallTimeoutMs = 180000;
        private const int FinalizationMoveAttempts = 15;
        private const int FinalizationInitialRetryDelayMs = 100;
        private const int FinalizationMaximumRetryDelayMs = 1000;

        public bool EnsureInstalled(out InstalledServerRuntime runtime, out string error)
        {
            if (!InstallGate.Wait(0))
            {
                runtime = null;
                error = "A server runtime installation is already in progress.";
                return false;
            }
            try
            {
                var result = EnsureInstalledCoreAsync(
                    null,
                    false,
                    CancellationToken.None).GetAwaiter().GetResult();
                runtime = result.Runtime;
                error = result.Error;
                return result.Success;
            }
            finally
            {
                InstallGate.Release();
            }
        }

        public async Task<ServerRuntimeInstallResult> EnsureInstalledAsync(
            IProgress<ServerStartProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            await InstallGate.WaitAsync(cancellationToken);
            try
            {
                return await EnsureInstalledCoreAsync(
                    progress,
                    true,
                    cancellationToken);
            }
            finally
            {
                InstallGate.Release();
            }
        }

        private async Task<ServerRuntimeInstallResult> EnsureInstalledCoreAsync(
            IProgress<ServerStartProgress> progress,
            bool runCommandsAsynchronously,
            CancellationToken cancellationToken)
        {
            progress?.Report(new ServerStartProgress(0.05f, "Preparing server runtime…"));
            try
            {
                string source = AssetPathUtility.GetMcpServerPackageSource();
                string version = AssetPathUtility.GetPackageVersion();
                string runtimeRoot = GetRuntimeRoot();
                string runtimeName = BuildRuntimeName(version, source);
                string target = Path.Combine(runtimeRoot, runtimeName);
                bool forceRefresh = AssetPathUtility.ShouldForceUvxRefresh();
                RuntimePlatform platform = Application.platform;

                if (!forceRefresh
                    && TryCreateRuntime(target, version, source, out var existingRuntime))
                {
                    progress?.Report(new ServerStartProgress(0.88f, "Server runtime is ready"));
                    return ServerRuntimeInstallResult.Succeeded(existingRuntime);
                }

                string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
                string uvPath = BuildUvPathFromUvx(uvxPath);
                if (string.IsNullOrWhiteSpace(uvPath))
                {
                    return ServerRuntimeInstallResult.Failed(
                        "uv is not installed or could not be located.");
                }

                Directory.CreateDirectory(runtimeRoot);
                string staging = Path.Combine(
                    runtimeRoot,
                    runtimeName + ".staging-" + Guid.NewGuid().ToString("N"));
                try
                {
                    string projectRoot = GetProjectRoot();
                    string pathPrepend = GetPlatformSpecificPathPrepend();

                    progress?.Report(new ServerStartProgress(
                        0.12f,
                        "Creating isolated Python environment…"));
                    string venvArgs = BuildVenvArguments(staging, platform);
                    CommandResult venvResult = await RunCommandAsync(
                        uvPath,
                        venvArgs,
                        projectRoot,
                        InstallTimeoutMs,
                        pathPrepend,
                        runCommandsAsynchronously,
                        cancellationToken);
                    if (!venvResult.Success)
                    {
                        return ServerRuntimeInstallResult.Failed(
                            BuildCommandError(
                                "create the server virtual environment",
                                venvResult.Stdout,
                                venvResult.Stderr));
                    }

                    progress?.Report(new ServerStartProgress(
                        0.32f,
                        "Installing MCP server dependencies…"));
                    string pythonPath = GetPythonExecutable(staging, platform);
                    string prerelease = source != null && source.Contains(">=0.0.0a0")
                        ? " --prerelease=allow"
                        : string.Empty;
                    string installArgs =
                        $"pip install --python {Quote(pythonPath)} --reinstall{prerelease} {Quote(source)}";
                    CommandResult installResult = await RunCommandAsync(
                        uvPath,
                        installArgs,
                        projectRoot,
                        InstallTimeoutMs,
                        pathPrepend,
                        runCommandsAsynchronously,
                        cancellationToken);
                    if (!installResult.Success)
                    {
                        return ServerRuntimeInstallResult.Failed(
                            BuildCommandError(
                                "install the MCP server runtime",
                                installResult.Stdout,
                                installResult.Stderr));
                    }

                    if (!TryCreateRuntime(
                        staging,
                        version,
                        source,
                        platform,
                        out var stagedRuntime))
                    {
                        return ServerRuntimeInstallResult.Failed(
                            "The server package installed, but its mcp-for-unity and " +
                            "mcp-for-unity-supervisor entry points were not found.");
                    }

                    progress?.Report(new ServerStartProgress(
                        0.78f,
                        "Validating installed server…"));
                    ProbeResult probeResult = await ProbeRuntimeAsync(
                        stagedRuntime,
                        pythonPath,
                        projectRoot,
                        pathPrepend,
                        platform,
                        runCommandsAsynchronously,
                        cancellationToken);
                    if (!probeResult.Success)
                    {
                        return ServerRuntimeInstallResult.Failed(probeResult.Error);
                    }

                    progress?.Report(new ServerStartProgress(
                        0.92f,
                        "Finalizing server runtime…"));
                    if (Directory.Exists(target))
                    {
                        EnsureChildOfRuntimeRoot(target);
                        Directory.Delete(target, true);
                    }
                    await MoveDirectoryWithRetryAsync(
                        staging,
                        target,
                        cancellationToken);

                    if (!TryCreateRuntime(
                        target,
                        version,
                        source,
                        platform,
                        out var runtime))
                    {
                        return ServerRuntimeInstallResult.Failed(
                            "Installed server runtime failed final validation.");
                    }

                    var manifest = BuildRuntimeManifest(
                        runtime,
                        version,
                        source,
                        probeResult.PythonVersion,
                        platform == RuntimePlatform.WindowsEditor);
                    File.WriteAllText(runtime.ManifestPath, manifest.ToString());
                    progress?.Report(new ServerStartProgress(0.96f, "Server runtime installed"));
                    return ServerRuntimeInstallResult.Succeeded(runtime);
                }
                finally
                {
                    if (Directory.Exists(staging))
                    {
                        try
                        {
                            EnsureChildOfRuntimeRoot(staging);
                            Directory.Delete(staging, true);
                        }
                        catch
                        {
                            // A failed staging cleanup is harmless and remains under Library.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return ServerRuntimeInstallResult.Failed(
                    $"Failed to install project-local MCP server runtime: {ex.Message}");
            }
        }

        public bool TryGetInstalled(out InstalledServerRuntime runtime)
        {
            string source = AssetPathUtility.GetMcpServerPackageSource();
            string version = AssetPathUtility.GetPackageVersion();
            string target = Path.Combine(GetRuntimeRoot(), BuildRuntimeName(version, source));
            return TryCreateRuntime(target, version, source, Application.platform, out runtime);
        }

        private static bool TryCreateRuntime(
            string root,
            string version,
            string source,
            out InstalledServerRuntime runtime)
        {
            return TryCreateRuntime(root, version, source, Application.platform, out runtime);
        }

        private static bool TryCreateRuntime(
            string root,
            string version,
            string source,
            RuntimePlatform platform,
            out InstalledServerRuntime runtime)
        {
            runtime = null;
            string scripts = platform == RuntimePlatform.WindowsEditor
                ? Path.Combine(root, "Scripts")
                : Path.Combine(root, "bin");
            string extension = platform == RuntimePlatform.WindowsEditor ? ".exe" : string.Empty;
            string server = Path.Combine(scripts, "mcp-for-unity" + extension);
            string supervisor = Path.Combine(scripts, "mcp-for-unity-supervisor" + extension);
            string manifest = Path.Combine(root, "runtime.json");
            if (!File.Exists(server) || !File.Exists(supervisor))
            {
                return false;
            }
            runtime = new InstalledServerRuntime(
                root,
                server,
                supervisor,
                manifest,
                version,
                source);
            return true;
        }

        private static string GetRuntimeRoot()
        {
            return Path.Combine(GetProjectRoot(), "Library", "MCPForUnity", "ServerRuntime");
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string BuildRuntimeName(string version, string source)
        {
            string safeVersion = string.IsNullOrWhiteSpace(version)
                ? "unknown"
                : version.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(source ?? string.Empty));
                var hash = new StringBuilder(12);
                for (int i = 0; i < 6; i++)
                {
                    hash.Append(bytes[i].ToString("x2"));
                }
                return safeVersion + "-" + hash;
            }
        }

        private static string BuildUvPathFromUvx(string uvxPath)
        {
            if (string.IsNullOrWhiteSpace(uvxPath)) return uvxPath;
            string directory = Path.GetDirectoryName(uvxPath);
            string extension = Path.GetExtension(uvxPath);
            string file = "uv" + extension;
            return string.IsNullOrEmpty(directory) ? file : Path.Combine(directory, file);
        }

        private static string GetPythonExecutable(
            string runtimeRoot,
            RuntimePlatform platform)
        {
            return platform == RuntimePlatform.WindowsEditor
                ? Path.Combine(runtimeRoot, "Scripts", "python.exe")
                : Path.Combine(runtimeRoot, "bin", "python");
        }

        private static string GetPlatformSpecificPathPrepend()
        {
            return new ServerCommandBuilder().GetPlatformSpecificPathPrepend();
        }

        internal static string BuildVenvArguments(string target, RuntimePlatform platform)
        {
            // Entry-point launchers and Unix shebangs normally retain absolute paths to the
            // environment where they were created. The installer validates in a staging
            // directory and then moves the environment, so it must always be relocatable.
            if (platform == RuntimePlatform.WindowsEditor)
            {
                // Microsoft Store Python launchers can break descendants out of a Windows
                // Job Object. A uv-managed interpreter remains in the job, so its memory is
                // included in accounting and hard limits.
                return $"venv --managed-python --python 3.12 --relocatable {Quote(target)}";
            }

            return $"venv --relocatable {Quote(target)}";
        }

        internal static JObject BuildRuntimeManifest(
            InstalledServerRuntime runtime,
            string version,
            string source,
            string pythonVersion,
            bool usesUvManagedPython)
        {
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["packageVersion"] = version,
                ["source"] = source,
                ["installedUtc"] = DateTime.UtcNow.ToString("O"),
                ["pythonVersion"] = pythonVersion,
                ["usesUvManagedPython"] = usesUvManagedPython,
                ["serverExecutable"] = runtime.ServerExecutable,
                ["supervisorExecutable"] = runtime.SupervisorExecutable
            };
        }

        private static Task MoveDirectoryWithRetryAsync(
            string source,
            string target,
            CancellationToken cancellationToken)
        {
            return MoveDirectoryWithRetryAsync(
                source,
                target,
                cancellationToken,
                FinalizationMoveAttempts,
                FinalizationInitialRetryDelayMs,
                FinalizationMaximumRetryDelayMs,
                Directory.Move,
                (delayMs, token) => Task.Delay(delayMs, token));
        }

        internal static async Task MoveDirectoryWithRetryAsync(
            string source,
            string target,
            CancellationToken cancellationToken,
            int maximumAttempts,
            int initialRetryDelayMs,
            int maximumRetryDelayMs,
            Action<string, string> moveDirectory,
            Func<int, CancellationToken, Task> delayAsync)
        {
            if (maximumAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
            }
            if (initialRetryDelayMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialRetryDelayMs));
            }
            if (maximumRetryDelayMs < initialRetryDelayMs)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumRetryDelayMs));
            }
            if (moveDirectory == null)
            {
                throw new ArgumentNullException(nameof(moveDirectory));
            }
            if (delayAsync == null)
            {
                throw new ArgumentNullException(nameof(delayAsync));
            }

            int retryDelayMs = initialRetryDelayMs;
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    moveDirectory(source, target);
                    return;
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (attempt == maximumAttempts)
                    {
                        throw BuildFinalizationException(source, target, maximumAttempts, ex);
                    }
                }
                catch (IOException ex)
                {
                    if (attempt == maximumAttempts)
                    {
                        throw BuildFinalizationException(source, target, maximumAttempts, ex);
                    }
                }

                await delayAsync(retryDelayMs, cancellationToken);
                retryDelayMs = Math.Min(retryDelayMs * 2, maximumRetryDelayMs);
            }
        }

        private static IOException BuildFinalizationException(
            string source,
            string target,
            int attempts,
            Exception innerException)
        {
            return new IOException(
                $"Could not finalize the MCP server runtime after {attempts} attempts " +
                $"by moving '{source}' to '{target}'. {innerException.Message}",
                innerException);
        }

        private static async Task<ProbeResult> ProbeRuntimeAsync(
            InstalledServerRuntime runtime,
            string pythonPath,
            string workingDirectory,
            string pathPrepend,
            RuntimePlatform platform,
            bool runCommandsAsynchronously,
            CancellationToken cancellationToken)
        {
            CommandResult pythonResult = await RunCommandAsync(
                pythonPath,
                "--version",
                workingDirectory,
                15000,
                pathPrepend,
                runCommandsAsynchronously,
                cancellationToken);
            if (!pythonResult.Success)
            {
                return ProbeResult.Failed(BuildCommandError(
                    "validate the installed Python runtime",
                    pythonResult.Stdout,
                    pythonResult.Stderr));
            }
            string pythonVersion = string.IsNullOrWhiteSpace(pythonResult.Stdout)
                ? pythonResult.Stderr.Trim()
                : pythonResult.Stdout.Trim();

            CommandResult serverResult = await RunCommandAsync(
                runtime.ServerExecutable,
                "--help",
                workingDirectory,
                30000,
                pathPrepend,
                runCommandsAsynchronously,
                cancellationToken);
            if (!serverResult.Success)
            {
                return ProbeResult.Failed(BuildCommandError(
                    "validate the installed MCP server entry point",
                    serverResult.Stdout,
                    serverResult.Stderr));
            }

            CommandResult supervisorResult = await RunCommandAsync(
                runtime.SupervisorExecutable,
                "--help",
                workingDirectory,
                15000,
                pathPrepend,
                runCommandsAsynchronously,
                cancellationToken);
            if (!supervisorResult.Success)
            {
                return ProbeResult.Failed(BuildCommandError(
                    "validate the installed supervisor entry point",
                    supervisorResult.Stdout,
                    supervisorResult.Stderr));
            }

            return ProbeResult.Succeeded(pythonVersion);
        }

        private static Task<CommandResult> RunCommandAsync(
            string file,
            string args,
            string workingDirectory,
            int timeoutMs,
            string pathPrepend,
            bool runAsynchronously,
            CancellationToken cancellationToken)
        {
            if (!runAsynchronously)
            {
                return Task.FromResult(RunCommand(
                    file,
                    args,
                    workingDirectory,
                    timeoutMs,
                    pathPrepend,
                    cancellationToken));
            }

            return Task.Run(() => RunCommand(
                file,
                args,
                workingDirectory,
                timeoutMs,
                pathPrepend,
                cancellationToken),
                cancellationToken);
        }

        private static CommandResult RunCommand(
            string file,
            string args,
            string workingDirectory,
            int timeoutMs,
            string pathPrepend,
            CancellationToken cancellationToken)
        {
            bool success = ExecPath.TryRun(
                file,
                args,
                workingDirectory,
                out string stdout,
                out string stderr,
                timeoutMs,
                pathPrepend,
                cancellationToken);
            return new CommandResult(success, stdout, stderr);
        }

        private readonly struct CommandResult
        {
            public CommandResult(bool success, string stdout, string stderr)
            {
                Success = success;
                Stdout = stdout ?? string.Empty;
                Stderr = stderr ?? string.Empty;
            }

            public bool Success { get; }
            public string Stdout { get; }
            public string Stderr { get; }
        }

        private sealed class ProbeResult
        {
            private ProbeResult(bool success, string pythonVersion, string error)
            {
                Success = success;
                PythonVersion = pythonVersion ?? string.Empty;
                Error = error;
            }

            public bool Success { get; }
            public string PythonVersion { get; }
            public string Error { get; }

            public static ProbeResult Succeeded(string pythonVersion)
            {
                return new ProbeResult(true, pythonVersion, null);
            }

            public static ProbeResult Failed(string error)
            {
                return new ProbeResult(false, string.Empty, error);
            }
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string BuildCommandError(string action, string stdout, string stderr)
        {
            string details = string.Join(
                Environment.NewLine,
                new[] { stderr, stdout });
            return $"Failed to {action}.{Environment.NewLine}{details.Trim()}";
        }

        private static void EnsureChildOfRuntimeRoot(string path)
        {
            string root = Path.GetFullPath(GetRuntimeRoot())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to modify a runtime path outside the project Library directory.");
            }
        }
    }
}
