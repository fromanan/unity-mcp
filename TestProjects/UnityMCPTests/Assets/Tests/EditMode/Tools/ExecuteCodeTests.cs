using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MCPForUnity.Editor.Tools;
using static MCPForUnityTests.Editor.TestUtilities;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    public class ExecuteCodeTests
    {
        [SetUp]
        public void SetUp()
        {
            HandleCommandSync(new JObject { ["action"] = "clear_history" });
        }

        // ──────────────────── Execute: success cases ────────────────────

        [Test]
        public void Execute_ReturnString_ReturnsSuccess()
        {
            var result = Execute("return \"hello\";");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("hello", result["data"]["result"].Value<string>());
        }

        [Test]
        public void Execute_ReturnInt_ReturnsSuccess()
        {
            var result = Execute("return 42;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(42, result["data"]["result"].Value<int>());
        }

        [Test]
        public void Execute_ReturnNull_NoResultValue()
        {
            var result = Execute("int x = 1; return null;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            // data may contain compiler info but should not have a "result" key
            var data = result["data"] as JObject;
            if (data != null)
                Assert.IsNull(data["result"], "Expected no 'result' key when code returns null");
        }

        [Test]
        public void Execute_ReturnGameObject_UsesStableUnityReferenceSerialization()
        {
            var gameObject = new GameObject("ExecuteCodeSerializationTarget");
            try
            {
                var result = Execute(
                    "return UnityEngine.GameObject.Find(\"ExecuteCodeSerializationTarget\");");

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                Assert.AreEqual(gameObject.GetInstanceID(), result["data"]["result"]["instanceID"].Value<int>());
                Assert.AreEqual("ExecuteCodeSerializationTarget", result["data"]["result"]["name"].Value<string>());
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Execute_VoidReturn_Succeeds()
        {
            var result = Execute("UnityEngine.Debug.Log(\"test\"); return null;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
        }

        [Test]
        public void Execute_UnityAPI_CanAccessSceneManager()
        {
            var result = Execute(
                "var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();\n" +
                "return scene.name;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsNotNull(result["data"]["result"]);
        }

        [Test]
        public void Execute_Generics_ListOfString()
        {
            var result = Execute(
                "var list = new System.Collections.Generic.List<string>();\n" +
                "list.Add(\"a\"); list.Add(\"b\");\n" +
                "return list;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var arr = result["data"]["result"] as JArray;
            Assert.IsNotNull(arr, "Expected array result");
            Assert.AreEqual(2, arr.Count);
        }

        [Test]
        public void Execute_LINQ_SelectWorks()
        {
            var result = Execute(
                "var nums = new int[] { 1, 2, 3 };\n" +
                "return nums.Select(n => n * 2).ToList();");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var arr = result["data"]["result"] as JArray;
            Assert.IsNotNull(arr);
            Assert.AreEqual(3, arr.Count);
            Assert.AreEqual(2, arr[0].Value<int>());
            Assert.AreEqual(6, arr[2].Value<int>());
        }

        [Test]
        public void Execute_Dictionary_ReturnsStructured()
        {
            var result = Execute(
                "var dict = new Dictionary<string, int> { { \"a\", 1 }, { \"b\", 2 } };\n" +
                "return dict;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsNotNull(result["data"]["result"]);
        }

        [Test]
        public void Execute_ReturnTaskOfInt_AwaitsAndSerializesResult()
        {
            JObject result = Execute("return System.Threading.Tasks.Task.FromResult(17);");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(17, result["data"]["result"].Value<int>());
        }

        [Test]
        public void Execute_IdenticalCode_ReusesCompiledSnippet()
        {
            JObject before = GetStatus();
            string value = System.Guid.NewGuid().ToString("N");
            string code = $"return \"{value}\";";

            JObject first = Execute(code);
            JObject second = Execute(code);
            JObject after = GetStatus();

            Assert.IsTrue(first.Value<bool>("success"), first.ToString());
            Assert.IsTrue(second.Value<bool>("success"), second.ToString());
            Assert.IsFalse(first["data"]["cacheHit"].Value<bool>());
            Assert.IsTrue(second["data"]["cacheHit"].Value<bool>());
            Assert.AreEqual(
                before["data"]["uniqueCompilations"].Value<int>() + 1,
                after["data"]["uniqueCompilations"].Value<int>());
        }

        // ──────────────────── Execute: error cases ────────────────────

        [Test]
        public void Execute_CompilationError_ReturnsErrors()
        {
            var result = Execute("int x = \"not an int\";");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Compilation failed", result.Value<string>("error"));
            Assert.IsNotNull(result["data"]["errors"]);
        }

        [Test]
        public void Execute_RuntimeException_ReturnsError()
        {
            var result = Execute("throw new System.Exception(\"boom\");");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("boom", result.Value<string>("error"));
        }

        [Test]
        public void Execute_MissingCode_ReturnsError()
        {
            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "execute"
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("code", result.Value<string>("error").ToLowerInvariant());
        }

        [Test]
        public void Execute_EmptyCode_ReturnsError()
        {
            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "execute",
                ["code"] = "   "
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
        }

        // ──────────────────── Safety checks ────────────────────

        [Test]
        public void Execute_SafetyChecks_BlocksFileDelete()
        {
            var result = Execute("System.IO.File.Delete(\"x\");");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Blocked pattern", result.Value<string>("error"));
        }

        [Test]
        public void Execute_SafetyChecks_BlocksProcessStart()
        {
            var result = Execute("Process.Start(\"cmd\");");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Blocked pattern", result.Value<string>("error"));
        }

        [Test]
        public void Execute_SafetyChecks_BlocksInfiniteLoop()
        {
            var result = Execute("while (true) { }");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Blocked pattern", result.Value<string>("error"));
        }

        [Test]
        public void Execute_SafetyChecks_BlocksAssetDatabaseObjectLoad()
        {
            JObject result = Execute(
                "return UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(\"Assets/Test.mat\");");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("AssetDatabase.LoadAssetAtPath", result.Value<string>("error"));
        }

        [Test]
        public void Execute_SafetyChecks_BlocksDetachedContinuation()
        {
            JObject result = Execute(
                "return System.Threading.Tasks.Task.FromResult(1).ContinueWith(value => value.Result);");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("detached work pattern", result.Value<string>("error"));
            StringAssert.Contains("Return the Task", result.Value<string>("error"));
        }

        [Test]
        public void Execute_SafetyChecksDisabled_AllowsBlockedPattern()
        {
            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "execute",
                ["code"] = "while (true) { break; }  return null;",
                ["safety_checks"] = false
            }));

            if (!result.Value<bool>("success"))
            {
                var error = result.Value<string>("error") ?? "";
                Assert.IsFalse(error.Contains("Blocked pattern"),
                    "Safety checks should be disabled but still blocked");
            }
        }

        // ──────────────────── History ────────────────────

        [Test]
        public void GetHistory_Empty_ReturnsZero()
        {
            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "get_history"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(0, result["data"]["total"].Value<int>());
        }

        [Test]
        public void GetHistory_AfterExecution_RecordsEntry()
        {
            Execute("return 1;");

            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "get_history"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(1, result["data"]["total"].Value<int>());
            var entries = result["data"]["entries"] as JArray;
            Assert.IsNotNull(entries);
            Assert.AreEqual(1, entries.Count);
            Assert.IsTrue(entries[0]["success"].Value<bool>());
        }

        [Test]
        public void GetHistory_Limit_RespectsParameter()
        {
            Execute("return 1;");
            Execute("return 2;");
            Execute("return 3;");

            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "get_history",
                ["limit"] = 2
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(3, result["data"]["total"].Value<int>());
            var entries = result["data"]["entries"] as JArray;
            Assert.AreEqual(2, entries.Count);
        }

        [Test]
        public void ClearHistory_RemovesAll()
        {
            Execute("return 1;");
            Execute("return 2;");

            var clearResult = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "clear_history"
            }));
            Assert.IsTrue(clearResult.Value<bool>("success"), clearResult.ToString());

            var historyResult = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "get_history"
            }));
            Assert.AreEqual(0, historyResult["data"]["total"].Value<int>());
        }

        // ──────────────────── Replay ────────────────────

        [Test]
        public void Replay_ValidIndex_ReExecutes()
        {
            Execute("return 42;");

            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "replay",
                ["index"] = 0
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(42, result["data"]["result"].Value<int>());
        }

        [Test]
        public void Replay_InvalidIndex_ReturnsError()
        {
            Execute("return 1;");

            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "replay",
                ["index"] = 99
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Invalid history index", result.Value<string>("error"));
        }

        [Test]
        public void Replay_EmptyHistory_ReturnsError()
        {
            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "replay",
                ["index"] = 0
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
        }

        // ──────────────────── Action validation ────────────────────

        [Test]
        public void UnknownAction_ReturnsError()
        {
            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "invalid_action"
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Unknown action", result.Value<string>("error"));
        }

        [Test]
        public void NullParams_ReturnsError()
        {
            var result = ToJObject(HandleCommandSync(null));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
        }

        [Test]
        public void GetStatus_ReturnsCompilationBudgetAndCacheTelemetry()
        {
            JObject result = GetStatus();

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(
                ExecuteCode.MaxUniqueCompilationsPerDomain,
                result["data"]["compilationLimit"].Value<int>());
            Assert.GreaterOrEqual(result["data"]["cachedSnippets"].Value<int>(), 0);
            Assert.GreaterOrEqual(result["data"]["roslynMetadataReferencesCached"].Value<int>(), 0);
        }

        [Test]
        public void Execute_CompilationBudgetReached_RejectsUniqueCodeBeforeCompilation()
        {
            FieldInfo countField = typeof(ExecuteCode).GetField(
                "_uniqueCompilationCount",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(countField);
            int originalCount = (int)countField.GetValue(null);

            try
            {
                countField.SetValue(null, ExecuteCode.MaxUniqueCompilationsPerDomain);
                JObject result = Execute($"return \"{System.Guid.NewGuid():N}\";");

                Assert.IsFalse(result.Value<bool>("success"), result.ToString());
                StringAssert.Contains("domain limit", result.Value<string>("error"));
                Assert.AreEqual(
                    ExecuteCode.MaxUniqueCompilationsPerDomain,
                    result["data"]["uniqueCompilations"].Value<int>());
            }
            finally
            {
                countField.SetValue(null, originalCount);
            }
        }

        // ──────────────────── CodeDom backend ────────────────────

        // Regression for CoplayDev/unity-mcp#1144: large projects (~100+ asmdefs) blew past the
        // Windows 32 KB CreateProcess limit because every reference became an inline /r: flag.
        // The fix routes references through a @responsefile, so this just verifies that the
        // codedom path still compiles and runs end-to-end.
        [Test]
        public void Execute_CodedomBackend_CompilesAndRuns()
        {
            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "execute",
                ["code"] = "return 1 + 1;",
                ["compiler"] = "codedom"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(2, result["data"]["result"].Value<int>());
            Assert.AreEqual("codedom", result["data"]["compiler"].Value<string>());
        }

        [Test]
        public void Execute_CodedomBackend_ResolvesUnityTypes()
        {
            var result = ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "execute",
                ["code"] = "return UnityEngine.Application.unityVersion;",
                ["compiler"] = "codedom"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsNotNull(result["data"]["result"]);
        }

        // ──────────────────── Helpers ────────────────────

        private static JObject Execute(string code)
        {
            return ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "execute",
                ["code"] = code
            }));
        }

        private static JObject GetStatus()
        {
            return ToJObject(HandleCommandSync(new JObject
            {
                ["action"] = "get_status"
            }));
        }

        private static object HandleCommandSync(JObject parameters)
        {
            return ExecuteCode.HandleCommand(parameters).GetAwaiter().GetResult();
        }

    }
}
