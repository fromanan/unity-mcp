using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    /// <summary>
    /// Launches commands in platform-specific terminal windows.
    /// Supports macOS Terminal, Windows cmd, and Linux terminal emulators.
    /// </summary>
    public class TerminalLauncher : ITerminalLauncher
    {
        /// <inheritdoc/>
        public string GetProjectRootPath()
        {
            try
            {
                // Application.dataPath is ".../<Project>/Assets"
                return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            }
            catch
            {
                return Application.dataPath;
            }
        }

        /// <inheritdoc/>
        public System.Diagnostics.ProcessStartInfo CreateHeadlessProcessStartInfo(string command, string logFilePath)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Command cannot be empty", nameof(command));
            if (string.IsNullOrWhiteSpace(logFilePath))
                throw new ArgumentException("Log file path cannot be empty", nameof(logFilePath));

            command = command.Replace("\r", "").Replace("\n", "");

            string logDir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            List<string> tokens = SplitCommandLine(command);
            if (tokens.Count == 0)
            {
                throw new ArgumentException("Command does not contain an executable", nameof(command));
            }

            return new System.Diagnostics.ProcessStartInfo
            {
                FileName = tokens[0],
                Arguments = string.Join(" ", tokens.Skip(1).Select(QuoteProcessArgument)),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        internal static List<string> SplitCommandLine(string command)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            int backslashes = 0;

            foreach (char ch in command)
            {
                if (ch == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (ch == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    current.Append('\\', backslashes);
                    backslashes = 0;
                }

                if (char.IsWhiteSpace(ch) && !quoted)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(ch);
            }

            if (backslashes > 0)
            {
                current.Append('\\', backslashes);
            }
            if (quoted)
            {
                throw new ArgumentException("Command contains an unmatched quote", nameof(command));
            }
            if (current.Length > 0)
            {
                result.Add(current.ToString());
            }
            return result;
        }

        internal static string QuoteProcessArgument(string argument)
        {
            if (string.IsNullOrEmpty(argument))
            {
                return "\"\"";
            }
            if (!argument.Any(ch => char.IsWhiteSpace(ch) || ch == '"'))
            {
                return argument;
            }

            var result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char ch in argument)
            {
                if (ch == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (ch == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }
                result.Append('\\', backslashes);
                backslashes = 0;
                result.Append(ch);
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        /// <inheritdoc/>
        public System.Diagnostics.ProcessStartInfo CreateTerminalProcessStartInfo(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Command cannot be empty", nameof(command));

            command = command.Replace("\r", "").Replace("\n", "");

#if UNITY_EDITOR_OSX
            // macOS: Avoid AppleScript (automation permission prompts). Use a .command script and open it.
            string scriptsDir = Path.Combine(GetProjectRootPath(), "Library", "MCPForUnity", "TerminalScripts");
            Directory.CreateDirectory(scriptsDir);
            string scriptPath = Path.Combine(scriptsDir, "mcp-terminal.command");
            File.WriteAllText(
                scriptPath,
                "#!/bin/bash\n" +
                "set -e\n" +
                "clear\n" +
                $"{command}\n");
            ExecPath.TryRun("/bin/chmod", $"+x \"{scriptPath}\"", Application.dataPath, out _, out _, 3000);
            return new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                Arguments = $"-a Terminal \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
#elif UNITY_EDITOR_WIN
            // Windows: Avoid brittle nested-quote escaping by writing a .cmd script and starting it in a new window.
            string scriptsDir = Path.Combine(GetProjectRootPath(), "Library", "MCPForUnity", "TerminalScripts");
            Directory.CreateDirectory(scriptsDir);
            string scriptPath = Path.Combine(scriptsDir, "mcp-terminal.cmd");
            File.WriteAllText(
                scriptPath,
                "@echo off\r\n" +
                "cls\r\n" +
                command + "\r\n");
            return new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"MCP Server\" cmd.exe /k \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
#else
            // Linux: Try common terminal emulators.
            // ProcessStartInfo passes the argument string directly to the terminal, so we only
            // need to escape for the double-quoted bash -c payload — no inner single quotes.
            string script = $"{command}; exec bash";
            string escapedScriptForArg = script
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
            string bashCmdArgs = $"bash -c \"{escapedScriptForArg}\"";

            string[] terminals = { "gnome-terminal", "xterm", "konsole", "xfce4-terminal" };
            string terminalCmd = null;

            foreach (var term in terminals)
            {
                try
                {
                    var which = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = term,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    });
                    which.WaitForExit(5000); // Wait for up to 5 seconds, the command is typically instantaneous
                    if (which.ExitCode == 0)
                    {
                        terminalCmd = term;
                        break;
                    }
                }
                catch { }
            }

            if (terminalCmd == null)
            {
                terminalCmd = "xterm"; // Fallback
            }

            // Different terminals have different argument formats
            string args;
            if (terminalCmd == "gnome-terminal")
            {
                args = $"-- {bashCmdArgs}";
            }
            else if (terminalCmd == "konsole")
            {
                args = $"-e {bashCmdArgs}";
            }
            else if (terminalCmd == "xfce4-terminal")
            {
                // xfce4-terminal expects -e "command string" or -e command arg
                args = $"--hold -e \"{bashCmdArgs.Replace("\"", "\\\"")}\"";
            }
            else // xterm and others
            {
                args = $"-hold -e {bashCmdArgs}";
            }

            return new System.Diagnostics.ProcessStartInfo
            {
                FileName = terminalCmd,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };
#endif
        }
    }
}
