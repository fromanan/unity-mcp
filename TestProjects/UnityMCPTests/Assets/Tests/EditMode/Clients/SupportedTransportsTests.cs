using System.Linq;
using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Clients.Configurators;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Clients
{
    [TestFixture]
    public class SupportedTransportsTests
    {
        [Test]
        public void IMcpClientConfigurator_ExposesSupportedTransports()
        {
            var prop = typeof(IMcpClientConfigurator).GetProperty("SupportedTransports");
            Assert.IsNotNull(prop, "Must expose SupportedTransports");
        }

        [Test]
        public void ClaudeDesktop_SupportsStdioOnly()
        {
            var claude = new ClaudeDesktopConfigurator();
            CollectionAssert.Contains(claude.SupportedTransports.ToList(), ConfiguredTransport.Stdio);
            CollectionAssert.DoesNotContain(claude.SupportedTransports.ToList(), ConfiguredTransport.Http);
        }

        [Test]
        public void Codex_SupportsStdioAndHttp()
        {
            var codex = new CodexConfigurator();
            CollectionAssert.AreEqual(
                new[] { ConfiguredTransport.Stdio, ConfiguredTransport.Http },
                codex.SupportedTransports.ToList(),
                "Codex supports local stdio and Streamable HTTP MCP servers");
            Assert.IsTrue(
                codex.Client.SupportsHttpTransport,
                "Codex must be treated as HTTP-capable");
        }

        [Test]
        public void Codex_ManualSnippet_UsesHttpWhenHttpPreferred()
        {
            var cache = EditorConfigurationCache.Instance;
            bool original = cache.UseHttpTransport;
            try
            {
                cache.SetUseHttpTransport(true);
                string snippet = new CodexConfigurator().GetManualSnippet();

                StringAssert.Contains("url", snippet, "Codex snippet must configure HTTP");
                StringAssert.Contains("/mcp", snippet, "Codex HTTP URL must target the MCP endpoint");
                Assert.IsFalse(
                    snippet.Contains("command ="),
                    "HTTP configuration must not include a stdio command");
                Assert.IsTrue(cache.UseHttpTransport, "The global transport pref must be restored");
            }
            finally
            {
                cache.SetUseHttpTransport(original);
            }
        }

        [Test]
        public void Cursor_SupportsBothTransports()
        {
            var cursor = new CursorConfigurator();
            var list = cursor.SupportedTransports.ToList();
            CollectionAssert.Contains(list, ConfiguredTransport.Stdio);
            CollectionAssert.Contains(list, ConfiguredTransport.Http);
        }
    }
}
