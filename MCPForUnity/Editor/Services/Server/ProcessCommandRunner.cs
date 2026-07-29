using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Services.Server
{
    public sealed class ProcessCommandRunner : IProcessCommandRunner
    {
        public bool Run(string fileName, string arguments, out string stdout, out string stderr)
        {
            return ExecPath.TryRun(
                fileName,
                arguments,
                Application.dataPath,
                out stdout,
                out stderr);
        }
    }
}
