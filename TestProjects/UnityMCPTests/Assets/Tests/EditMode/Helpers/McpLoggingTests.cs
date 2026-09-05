using System;
using System.IO;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Helpers
{
    public class McpLoggingTests
    {
        [Test]
        public void LogDirectory_IsOutsideAssetsAndUnderLibrary()
        {
            StringAssert.Contains(
                Path.Combine("Library", "MCPForUnity", "Logs"),
                McpLogRecord.LogDirectory);
            StringAssert.DoesNotContain(
                Path.Combine("Assets", "UnityMCP", "Log"),
                McpLogRecord.LogDirectory);
        }

        [Test]
        public void DefaultRecord_StoresShapeWithoutParameterValues()
        {
            var parameters = new JObject
            {
                ["action"] = "inspect",
                ["path"] = "Assets/Secret/large.asset",
                ["items"] = new JArray(1, 2, 3),
                ["api_key"] = "super-secret"
            };

            JObject entry = McpLogRecord.CreateEntry(
                "manage_asset",
                parameters,
                "tool",
                "SUCCESS",
                12,
                null,
                false);

            Assert.AreEqual("inspect", entry.Value<string>("action"));
            var summary = (JObject)entry["params"];
            Assert.AreEqual(4, summary.Value<int>("count"));
            Assert.AreEqual(4, ((JArray)summary["fields"]).Count);
            string serialized = entry.ToString();
            StringAssert.DoesNotContain("Assets/Secret/large.asset", serialized);
            StringAssert.DoesNotContain("super-secret", serialized);
            StringAssert.Contains("sizeUnit", serialized);
        }

        [Test]
        public void OptInRecord_RedactsSecretsAndBoundsLargeText()
        {
            var parameters = new JObject
            {
                ["action"] = "inspect",
                ["api_key"] = "super-secret",
                ["token"] = "generic-secret-token",
                ["payload"] = new string('p', 10000),
                ["nested"] = new JObject
                {
                    ["access_token"] = "nested-secret"
                }
            };
            string error = "authorization=error-secret " + new string('e', 10000);

            JObject entry = McpLogRecord.CreateEntry(
                "manage_asset",
                parameters,
                "tool",
                "ERROR",
                12,
                error,
                true);

            string serialized = entry.ToString();
            StringAssert.DoesNotContain("super-secret", serialized);
            StringAssert.DoesNotContain("generic-secret-token", serialized);
            StringAssert.DoesNotContain("nested-secret", serialized);
            StringAssert.DoesNotContain("error-secret", serialized);
            StringAssert.Contains("[REDACTED]", serialized);
            Assert.LessOrEqual(entry.Value<string>("error").Length, 4097);
            Assert.LessOrEqual(entry["params"].ToString().Length, 20000);
        }

        [Test]
        public void DisabledDebugGuard_SkipsMessageConstruction()
        {
            bool original = McpLog.DebugEnabled;
            int messageFactoryCalls = 0;
            Func<string> buildMessage = () =>
            {
                messageFactoryCalls++;
                return "expensive";
            };

            try
            {
                McpLog.SetDebugLoggingEnabled(false);
                if (McpLog.DebugEnabled)
                {
                    McpLog.Debug(buildMessage());
                }

                Assert.AreEqual(0, messageFactoryCalls);
            }
            finally
            {
                McpLog.SetDebugLoggingEnabled(original);
            }
        }
    }
}
