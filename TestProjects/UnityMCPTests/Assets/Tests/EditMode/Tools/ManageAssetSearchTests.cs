using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace MCPForUnityTests.Editor.Tools
{
    [TestFixture]
    public class ManageAssetSearchTests
    {
        private const string Folder = "Assets/Temp/McpManageAssetTests";

        [OneTimeSetUp]
        public void CreateFixtureAssets()
        {
            Directory.CreateDirectory(Folder);
            for (int i = 0; i < 105; i++)
            {
                File.WriteAllText(
                    $"{Folder}/McpSearchFixture_{i:D3}.txt",
                    $"fixture {i}");
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        [OneTimeTearDown]
        public void DeleteFixtureAssets()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        [Test]
        public void Search_InvalidFolder_RefusesToBroadenScope()
        {
            var response = ManageAsset.HandleCommand(new JObject
            {
                ["action"] = "search",
                ["path"] = "Assets/Temp/FolderThatDoesNotExist"
            }) as ErrorResponse;

            Assert.IsNotNull(response);
            StringAssert.Contains("Refusing to broaden", response.Error);
        }

        [Test]
        public void Search_SnakeCaseAliases_PageBeforeHydration()
        {
            JObject data = Search(new JObject
            {
                ["page_size"] = 3,
                ["page_number"] = 2,
                ["generate_preview"] = false
            });

            Assert.AreEqual(3, data.Value<int>("pageSize"));
            Assert.AreEqual(2, data.Value<int>("pageNumber"));
            Assert.AreEqual(105, data.Value<int>("totalAssets"));
            Assert.IsTrue(data.Value<bool>("hasNextPage"));
            var assets = (JArray)data["assets"];
            Assert.AreEqual(3, assets.Count);
            Assert.IsTrue(assets.All(
                asset => asset.Value<int>("instanceID") == 0));
        }

        [Test]
        public void Search_OrdinaryPage_IsCappedAtOneHundred()
        {
            JObject data = Search(new JObject
            {
                ["pageSize"] = 1000,
                ["generatePreview"] = false
            });

            Assert.AreEqual(100, data.Value<int>("pageSize"));
            Assert.AreEqual(100, ((JArray)data["assets"]).Count);
            Assert.IsTrue(data.Value<bool>("hasNextPage"));
        }

        [Test]
        public void Search_PreviewPage_IsCappedAtTenAndLoadsOnlyThatPage()
        {
            JObject data = Search(new JObject
            {
                ["page_size"] = 1000,
                ["generate_preview"] = true
            });

            Assert.AreEqual(10, data.Value<int>("pageSize"));
            var assets = (JArray)data["assets"];
            Assert.AreEqual(10, assets.Count);
            Assert.IsTrue(assets.All(
                asset => asset.Value<int>("instanceID") != 0));
        }

        [Test]
        public void Search_DefaultPageSize_IsFifty()
        {
            JObject data = Search(new JObject());
            Assert.AreEqual(50, data.Value<int>("pageSize"));
            Assert.AreEqual(50, ((JArray)data["assets"]).Count);
        }

        private static JObject Search(JObject additional)
        {
            var parameters = new JObject
            {
                ["action"] = "search",
                ["path"] = Folder,
                ["search_pattern"] = "McpSearchFixture"
            };
            parameters.Merge(additional);
            var response = ManageAsset.HandleCommand(parameters) as SuccessResponse;
            Assert.IsNotNull(response);
            return JObject.FromObject(response.Data);
        }
    }
}
