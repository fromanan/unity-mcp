using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using MCPForUnity.Editor.Tools;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ReadConsoleTests
    {
        [Test]
        public void HandleCommand_Clear_Works()
        {
            // Arrange
            // Ensure there's something to clear
            string messageToClear = $"Log to clear {Guid.NewGuid()}";
            Debug.Log(messageToClear);
            
            // Verify content exists before clear
            var getBefore = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "get", ["types"] = new JArray { "error", "warning", "log" }, ["format"] = "detailed", ["count"] = 1000 }));
            Assert.IsTrue(getBefore.Value<bool>("success"), getBefore.ToString());
            var entriesBefore = getBefore["data"] as JArray;
            
            // Ideally we'd assert count > 0, but other tests/system logs might affect this.
            // Just ensuring the call doesn't fail is a baseline, but let's try to be stricter if possible.
            // Since we just logged, there should be at least one entry.
            Assert.IsTrue(
                entriesBefore != null
                && entriesBefore.Any(entry =>
                    entry["message"]?.ToString().Contains(messageToClear) == true),
                "Setup failed: the test log should be present.");

            // Act
            var result = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "clear" }));

            // Assert
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            
            // Verify clear effect
            var getAfter = ToJObject(ReadConsole.HandleCommand(new JObject { ["action"] = "get", ["types"] = new JArray { "error", "warning", "log" }, ["format"] = "detailed", ["count"] = 1000 }));
            Assert.IsTrue(getAfter.Value<bool>("success"), getAfter.ToString());
            var entriesAfter = getAfter["data"] as JArray;
            Assert.IsTrue(
                entriesAfter == null
                || entriesAfter.All(entry =>
                    entry["message"]?.ToString().Contains(messageToClear) != true),
                "The entry that existed before clear should be gone. Other editor logs may arrive asynchronously.");
        }

        [Test]
        public void HandleCommand_Get_Works()
        {
            // Arrange
            string uniqueMessage = $"Test Log Message {Guid.NewGuid()}";
            Debug.Log(uniqueMessage);
            
            var paramsObj = new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray { "error", "warning", "log" },
                ["format"] = "detailed",
                ["count"] = 1000 // Fetch enough to likely catch our message
            };

            // Act
            var result = ToJObject(ReadConsole.HandleCommand(paramsObj));

            // Assert
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var data = result["data"] as JArray;
            Assert.IsNotNull(data, "Data array should not be null.");
            Assert.IsTrue(data.Count > 0, "Should retrieve at least one log entry.");

            // Verify content
            bool found = false;
            foreach (var entry in data)
            {
                if (entry["message"]?.ToString().Contains(uniqueMessage) == true)
                {
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found, $"The unique log message '{uniqueMessage}' was not found in retrieved logs.");
        }

        [Test]
        public void HandleCommand_Get_PreservesMultilineMessageBody()
        {
            string id = Guid.NewGuid().ToString();
            string firstLine = $"First line {id}";
            string secondLine = $"Second line {id}";
            Debug.Log($"{firstLine}\n\n{secondLine}");

            var paramsObj = new JObject
            {
                ["action"] = "get",
                ["types"] = new JArray { "error", "warning", "log" },
                ["format"] = "detailed",
                ["count"] = 1000
            };

            var result = ToJObject(ReadConsole.HandleCommand(paramsObj));
            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var data = result["data"] as JArray;
            Assert.IsNotNull(data, "Data array should not be null.");

            string message = null;
            foreach (var entry in data)
            {
                string candidate = entry["message"]?.ToString();
                if (candidate != null && candidate.Contains(firstLine))
                {
                    message = candidate;
                    break;
                }
            }

            Assert.IsNotNull(message, "Multi-line log entry was not found.");
            StringAssert.Contains($"{firstLine}\n\n{secondLine}", message);
            StringAssert.DoesNotContain("UnityEngine.Debug", message);
        }

        [Test]
        public void OpaqueCursor_RoundTripsRawIndexAndRejectsFilterMismatch()
        {
            MethodInfo buildFingerprint = typeof(ReadConsole).GetMethod(
                "BuildFilterFingerprint",
                BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo buildCursor = typeof(ReadConsole).GetMethod(
                "BuildCursor",
                BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo tryResolve = typeof(ReadConsole).GetMethod(
                "TryResolveCursor",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(buildFingerprint);
            Assert.IsNotNull(buildCursor);
            Assert.IsNotNull(tryResolve);

            string fingerprint = (string)buildFingerprint.Invoke(
                null,
                new object[] { new List<string> { "warning", "error" }, "needle" });
            string cursor = (string)buildCursor.Invoke(
                null,
                new object[] { 123, fingerprint });
            object[] matchingArgs = { cursor, fingerprint, 0, 0, null };

            bool resolved = (bool)tryResolve.Invoke(null, matchingArgs);

            Assert.IsTrue(resolved);
            Assert.AreEqual(123, matchingArgs[2]);
            Assert.AreEqual(0, matchingArgs[3]);
            Assert.IsNull(matchingArgs[4]);

            object[] mismatchArgs = { cursor, "DIFFERENT", 0, 0, null };
            bool mismatchResolved = (bool)tryResolve.Invoke(null, mismatchArgs);
            Assert.IsFalse(mismatchResolved);
            StringAssert.Contains("does not match", mismatchArgs[4]?.ToString());
        }

        [Test]
        public void FilterFingerprint_IsStableAcrossTypeOrderingAndFilterCase()
        {
            MethodInfo buildFingerprint = typeof(ReadConsole).GetMethod(
                "BuildFilterFingerprint",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(buildFingerprint);

            string first = (string)buildFingerprint.Invoke(
                null,
                new object[] { new List<string> { "warning", "error" }, "Needle" });
            string second = (string)buildFingerprint.Invoke(
                null,
                new object[] { new List<string> { "error", "warning" }, "needle" });

            Assert.AreEqual(first, second);
        }
    }
}
