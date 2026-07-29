namespace MCPForUnity.Editor.Services.Server
{
    public interface IProcessCommandRunner
    {
        bool Run(string fileName, string arguments, out string stdout, out string stderr);
    }
}
