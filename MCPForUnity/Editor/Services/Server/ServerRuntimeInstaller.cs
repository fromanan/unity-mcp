using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
        private static readonly object InstallLock = new object();
        private const int InstallTimeoutMs = 180000;

        public bool EnsureInstalled(out InstalledServerRuntime runtime, out string error)
        {
            lock (InstallLock)
            {
                runtime = null;
                error = null;
                try
                {
                    string source = AssetPathUtility.GetMcpServerPackageSource();
                    string version = AssetPathUtility.GetPackageVersion();
                    string runtimeRoot = GetRuntimeRoot();
                    string runtimeName = BuildRuntimeName(version, source);
                    string target = Path.Combine(runtimeRoot, runtimeName);
                    bool forceRefresh = AssetPathUtility.ShouldForceUvxRefresh();

                    if (!forceRefresh && TryCreateRuntime(target, version, source, out runtime))
                    {
                        return true;
                    }

                    string uvxPath = MCPServiceLocator.Paths.GetUvxPath();
                    string uvPath = BuildUvPathFromUvx(uvxPath);
                    if (string.IsNullOrWhiteSpace(uvPath))
                    {
                        error = "uv is not installed or could not be located.";
                        return false;
                    }

                    Directory.CreateDirectory(runtimeRoot);
                    string staging = Path.Combine(
                        runtimeRoot,
                        runtimeName + ".staging-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        string stdout;
                        string stderr;
                        string projectRoot = GetProjectRoot();
                        string pathPrepend = GetPlatformSpecificPathPrepend();
                        string venvArgs = Application.platform == RuntimePlatform.WindowsEditor
                            // Microsoft Store Python launchers can break descendants out of a
                            // Windows Job Object. A uv-managed interpreter remains in the job,
                            // so its memory is included in accounting and hard limits.
                            ? $"venv --managed-python --python 3.12 {Quote(staging)}"
                            : $"venv {Quote(staging)}";
                        if (!ExecPath.TryRun(
                            uvPath,
                            venvArgs,
                            projectRoot,
                            out stdout,
                            out stderr,
                            InstallTimeoutMs,
                            pathPrepend))
                        {
                            error = BuildCommandError("create the server virtual environment", stdout, stderr);
                            return false;
                        }

                        string pythonPath = GetPythonExecutable(staging);
                        string prerelease = source != null && source.Contains(">=0.0.0a0")
                            ? " --prerelease=allow"
                            : string.Empty;
                        string installArgs =
                            $"pip install --python {Quote(pythonPath)} --reinstall{prerelease} {Quote(source)}";
                        if (!ExecPath.TryRun(
                            uvPath,
                            installArgs,
                            projectRoot,
                            out stdout,
                            out stderr,
                            InstallTimeoutMs,
                            pathPrepend))
                        {
                            error = BuildCommandError("install the MCP server runtime", stdout, stderr);
                            return false;
                        }

                        if (!TryCreateRuntime(staging, version, source, out var stagedRuntime))
                        {
                            error =
                                "The server package installed, but its mcp-for-unity and " +
                                "mcp-for-unity-supervisor entry points were not found.";
                            return false;
                        }
                        if (!TryProbeRuntime(
                            stagedRuntime,
                            pythonPath,
                            projectRoot,
                            pathPrepend,
                            out string pythonVersion,
                            out string probeError))
                        {
                            error = probeError;
                            return false;
                        }

                        var manifest = new JObject
                        {
                            ["schemaVersion"] = 1,
                            ["packageVersion"] = version,
                            ["source"] = source,
                            ["installedUtc"] = DateTime.UtcNow.ToString("O"),
                            ["pythonVersion"] = pythonVersion,
                            ["usesUvManagedPython"] =
                                Application.platform == RuntimePlatform.WindowsEditor,
                            ["serverExecutable"] = stagedRuntime.ServerExecutable,
                            ["supervisorExecutable"] = stagedRuntime.SupervisorExecutable
                        };
                        File.WriteAllText(
                            Path.Combine(staging, "runtime.json"),
                            manifest.ToString());

                        if (Directory.Exists(target))
                        {
                            EnsureChildOfRuntimeRoot(target);
                            Directory.Delete(target, true);
                        }
                        Directory.Move(staging, target);

                        if (!TryCreateRuntime(target, version, source, out runtime))
                        {
                            error = "Installed server runtime failed final validation.";
                            return false;
                        }
                        return true;
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
                    error = $"Failed to install project-local MCP server runtime: {ex.Message}";
                    return false;
                }
            }
        }

        public bool TryGetInstalled(out InstalledServerRuntime runtime)
        {
            string source = AssetPathUtility.GetMcpServerPackageSource();
            string version = AssetPathUtility.GetPackageVersion();
            string target = Path.Combine(GetRuntimeRoot(), BuildRuntimeName(version, source));
            return TryCreateRuntime(target, version, source, out runtime);
        }

        private static bool TryCreateRuntime(
            string root,
            string version,
            string source,
            out InstalledServerRuntime runtime)
        {
            runtime = null;
            string scripts = Application.platform == RuntimePlatform.WindowsEditor
                ? Path.Combine(root, "Scripts")
                : Path.Combine(root, "bin");
            string extension = Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : string.Empty;
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

        private static string GetPythonExecutable(string runtimeRoot)
        {
            return Application.platform == RuntimePlatform.WindowsEditor
                ? Path.Combine(runtimeRoot, "Scripts", "python.exe")
                : Path.Combine(runtimeRoot, "bin", "python");
        }

        private static string GetPlatformSpecificPathPrepend()
        {
            return new ServerCommandBuilder().GetPlatformSpecificPathPrepend();
        }

        private static bool TryProbeRuntime(
            InstalledServerRuntime runtime,
            string pythonPath,
            string workingDirectory,
            string pathPrepend,
            out string pythonVersion,
            out string error)
        {
            pythonVersion = string.Empty;
            error = null;
            if (!ExecPath.TryRun(
                pythonPath,
                "--version",
                workingDirectory,
                out string pythonStdout,
                out string pythonStderr,
                15000,
                pathPrepend))
            {
                error = BuildCommandError(
                    "validate the installed Python runtime",
                    pythonStdout,
                    pythonStderr);
                return false;
            }
            pythonVersion = string.IsNullOrWhiteSpace(pythonStdout)
                ? pythonStderr.Trim()
                : pythonStdout.Trim();

            if (!ExecPath.TryRun(
                runtime.ServerExecutable,
                "--help",
                workingDirectory,
                out string serverStdout,
                out string serverStderr,
                30000,
                pathPrepend))
            {
                error = BuildCommandError(
                    "validate the installed MCP server entry point",
                    serverStdout,
                    serverStderr);
                return false;
            }

            if (Application.platform == RuntimePlatform.WindowsEditor
                && !ExecPath.TryRun(
                    runtime.SupervisorExecutable,
                    "--help",
                    workingDirectory,
                    out string supervisorStdout,
                    out string supervisorStderr,
                    15000,
                    pathPrepend))
            {
                error = BuildCommandError(
                    "validate the installed Windows supervisor entry point",
                    supervisorStdout,
                    supervisorStderr);
                return false;
            }
            return true;
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
