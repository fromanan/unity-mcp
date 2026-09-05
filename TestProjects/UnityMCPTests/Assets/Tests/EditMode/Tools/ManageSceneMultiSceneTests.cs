using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using MCPForUnity.Editor.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Tests.EditMode.Tools
{
    [TestFixture]
    public class ManageSceneMultiSceneTests
    {
        private readonly List<Scene> _testScenes = new();
        private readonly List<string> _temporaryScenePaths = new();
        private readonly List<string> _temporaryFiles = new();
        private string _previewLeaseId;

        [SetUp]
        public void SetUp()
        {
            SceneSafetyState.ClearLeasesForTests();
            _testScenes.Clear();
            _temporaryScenePaths.Clear();
            _temporaryFiles.Clear();
            _previewLeaseId = null;
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_previewLeaseId))
            {
                SceneSafetyState.SceneLease lease = SceneSafetyState.FindLease(
                    _previewLeaseId,
                    null,
                    SceneSafetyState.PreviewLeaseKind);
                Scene previewScene = SceneSafetyState.ResolveLeaseScene(lease);
                if (previewScene.IsValid() && previewScene.isLoaded && EditorSceneManager.IsPreviewScene(previewScene))
                {
                    EditorSceneManager.ClosePreviewScene(previewScene);
                }
            }

            for (int i = _testScenes.Count - 1; i >= 0; i--)
            {
                Scene scene = _testScenes[i];
                if (scene.IsValid() && scene.isLoaded && SceneManager.sceneCount > 1)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            if (_temporaryScenePaths.Count > 0)
            {
                Scene activeScene = SceneManager.GetActiveScene();
                if (activeScene.IsValid() && _temporaryScenePaths.Any(path =>
                        string.Equals(activeScene.path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                foreach (string temporaryScenePath in _temporaryScenePaths)
                {
                    AssetDatabase.DeleteAsset(temporaryScenePath);
                }
            }
            foreach (string temporaryFile in _temporaryFiles)
            {
                if (File.Exists(temporaryFile))
                {
                    File.Delete(temporaryFile);
                }
            }
            SceneSafetyState.ClearLeasesForTests();
        }

        [Test]
        public void GetLoadedScenes_ReturnsAtLeastOne()
        {
            var p = new JObject { ["action"] = "get_loaded_scenes" };
            var result = ManageScene.HandleCommand(p);
            var r = result as JObject ?? JObject.FromObject(result);
            Assert.IsTrue(r.Value<bool>("success"), r.ToString());
            var scenes = r["data"]?["scenes"] as JArray;
            Assert.IsNotNull(scenes);
            Assert.GreaterOrEqual(scenes.Count, 1);
        }

        [Test]
        public void CloseScene_LastScene_ReturnsError()
        {
            if (SceneManager.sceneCount > 1)
            {
                Assert.Ignore("Test requires a single scene; editor has additive scenes open.");
                return;
            }
            var active = SceneManager.GetActiveScene();
            var p = new JObject
            {
                ["action"] = "close_scene",
                ["sceneName"] = active.name
            };
            var result = ManageScene.HandleCommand(p);
            var r = result as JObject ?? JObject.FromObject(result);
            Assert.IsFalse(r.Value<bool>("success"), "Should fail to close last scene");
        }

        [Test]
        public void MoveToScene_MissingTarget_ReturnsError()
        {
            var p = new JObject
            {
                ["action"] = "move_to_scene",
                ["sceneName"] = "SomeScene"
            };
            var result = ManageScene.HandleCommand(p);
            var r = result as JObject ?? JObject.FromObject(result);
            Assert.IsFalse(r.Value<bool>("success"));
        }

        [Test]
        public void MoveToScene_NonExistentGO_ReturnsError()
        {
            var p = new JObject
            {
                ["action"] = "move_to_scene",
                ["target"] = "NonExistentGO_99999",
                ["sceneName"] = "SomeScene"
            };
            var result = ManageScene.HandleCommand(p);
            var r = result as JObject ?? JObject.FromObject(result);
            Assert.IsFalse(r.Value<bool>("success"));
        }

        [Test]
        public void ModifyBuildSettings_RedirectsToManageBuild()
        {
            var p = new JObject
            {
                ["action"] = "modify_build_settings",
                ["scenePath"] = "Assets/Scenes/Test.unity",
                ["operation"] = "add"
            };
            var result = ManageScene.HandleCommand(p);
            var r = result as JObject ?? JObject.FromObject(result);
            Assert.IsFalse(r.Value<bool>("success"));
            Assert.IsTrue(r.Value<string>("error").Contains("manage_build"));
        }

        [Test]
        public void SetActiveScene_NotFound_ReturnsError()
        {
            var p = new JObject
            {
                ["action"] = "set_active_scene",
                ["sceneName"] = "NonExistentScene_99999"
            };
            var result = ManageScene.HandleCommand(p);
            var r = result as JObject ?? JObject.FromObject(result);
            Assert.IsFalse(r.Value<bool>("success"));
        }

        [Test]
        public void LoadAdditive_RecoveryPathRequiresPreview()
        {
            JObject parameters = new()
            {
                ["action"] = "load",
                ["path"] = "Assets/_Recovery/Recovered.unity",
                ["additive"] = true
            };

            object result = ManageScene.HandleCommand(parameters);
            JObject response = result as JObject ?? JObject.FromObject(result);

            Assert.IsFalse(response.Value<bool>("success"));
            Assert.AreEqual("recovery_scene_requires_preview", response.Value<string>("code"));
        }

        [Test]
        public void Save_MultipleScenesWithoutTarget_IsBlocked()
        {
            CreateAdditiveTestScene();

            object result = ManageScene.HandleCommand(new JObject { ["action"] = "save" });
            JObject response = result as JObject ?? JObject.FromObject(result);

            Assert.IsFalse(response.Value<bool>("success"));
            Assert.AreEqual("multiple_scenes_require_explicit_target", response.Value<string>("code"));
        }

        [Test]
        public void Save_ExplicitTargetWithMultipleScenes_SavesRequestedScene()
        {
            CreateAdditiveTestScene();
            Scene targetScene = SceneManager.GetSceneByPath(_temporaryScenePaths[0]);
            EditorSceneManager.MarkSceneDirty(targetScene);

            object result = ManageScene.HandleCommand(new JObject
            {
                ["action"] = "save",
                ["scenePath"] = targetScene.path
            });
            JObject response = result as JObject ?? JObject.FromObject(result);

            Assert.IsTrue(response.Value<bool>("success"), response.ToString());
            Assert.IsFalse(targetScene.isDirty);
            Assert.AreEqual(targetScene.path, response["data"]?.Value<string>("path"));
        }

        [Test]
        public void Save_CrossSceneReference_IsBlockedBeforeDiskWrite()
        {
            Scene sourceScene = CreateAdditiveTestScene();
            Scene targetScene = CreateAdditiveTestScene();
            GameObject source = new("Source");
            GameObject target = new("Target");
            SceneManager.MoveGameObjectToScene(source, sourceScene);
            SceneManager.MoveGameObjectToScene(target, targetScene);
            Rigidbody targetBody = target.AddComponent<Rigidbody>();
            FixedJoint sourceJoint = source.AddComponent<FixedJoint>();
            sourceJoint.connectedBody = targetBody;

            JObject parameters = new()
            {
                ["action"] = "save",
                ["sceneName"] = sourceScene.name
            };
            object result = ManageScene.HandleCommand(parameters);
            JObject response = result as JObject ?? JObject.FromObject(result);

            Assert.IsTrue(EditorSceneManager.DetectCrossSceneReferences(sourceScene));
            Assert.IsFalse(response.Value<bool>("success"));
            Assert.AreEqual("cross_scene_references_detected", response.Value<string>("code"));
        }

        [Test]
        public void TemporaryAdditiveLease_BlocksPlayModeEntry()
        {
            List<SceneSafetyState.SceneSnapshot> originalScenes = SceneSafetyState.CaptureScenes();
            Scene temporaryScene = CreateAdditiveTestScene();
            SceneSafetyState.RegisterLease(
                temporaryScene,
                SceneSafetyState.AdditiveLeaseKind,
                SceneSafetyState.TemporaryInspectionIntent,
                originalScenes,
                "request-1",
                "client-1",
                "unity-1");

            object result = ManageEditor.HandleCommand(new JObject { ["action"] = "play" });
            JObject response = result as JObject ?? JObject.FromObject(result);

            Assert.IsFalse(response.Value<bool>("success"));
            Assert.AreEqual("temporary_additive_scene_open", response.Value<string>("code"));
            Assert.IsFalse(EditorApplication.isPlaying);
        }

        [Test]
        public void CrossSceneReference_BlocksPlayModeEntry()
        {
            Scene sourceScene = CreateAdditiveTestScene();
            Scene targetScene = CreateAdditiveTestScene();
            GameObject source = new("Source");
            GameObject target = new("Target");
            SceneManager.MoveGameObjectToScene(source, sourceScene);
            SceneManager.MoveGameObjectToScene(target, targetScene);
            FixedJoint sourceJoint = source.AddComponent<FixedJoint>();
            sourceJoint.connectedBody = target.AddComponent<Rigidbody>();

            object result = ManageEditor.HandleCommand(new JObject { ["action"] = "play" });
            JObject response = result as JObject ?? JObject.FromObject(result);

            Assert.IsTrue(EditorSceneManager.DetectCrossSceneReferences(sourceScene));
            Assert.IsFalse(response.Value<bool>("success"));
            Assert.AreEqual("cross_scene_references_detected", response.Value<string>("code"));
            Assert.IsFalse(EditorApplication.isPlaying);
        }

        [Test]
        public void LoadPreview_UsesIsolatedPreviewSceneAndLease()
        {
            Scene originalActiveScene = SceneManager.GetActiveScene();
            int normalSceneCount = SceneManager.sceneCount;
            int previewSceneCount = EditorSceneManager.previewSceneCount;
            JObject loadParameters = new()
            {
                ["action"] = "load_preview",
                ["path"] = "Assets/Scenes/SampleScene.unity",
                ["mcpRequestId"] = "request-1",
                ["mcpClientSessionId"] = "client-1"
            };

            object loadResult = ManageScene.HandleCommand(loadParameters);
            JObject loadResponse = loadResult as JObject ?? JObject.FromObject(loadResult);
            Assert.IsTrue(loadResponse.Value<bool>("success"), loadResponse.ToString());
            _previewLeaseId = loadResponse["data"]?["lease"]?.Value<string>("leaseId");
            Assert.IsNotEmpty(_previewLeaseId);
            Assert.AreEqual(normalSceneCount, SceneManager.sceneCount);
            Assert.AreEqual(previewSceneCount + 1, EditorSceneManager.previewSceneCount);
            Assert.AreEqual(originalActiveScene, SceneManager.GetActiveScene());

            object closeResult = ManageScene.HandleCommand(new JObject
            {
                ["action"] = "close_preview_scene",
                ["leaseId"] = _previewLeaseId
            });
            JObject closeResponse = closeResult as JObject ?? JObject.FromObject(closeResult);
            Assert.IsTrue(closeResponse.Value<bool>("success"), closeResponse.ToString());
            _previewLeaseId = null;
            Assert.AreEqual(normalSceneCount, SceneManager.sceneCount);
            Assert.AreEqual(previewSceneCount, EditorSceneManager.previewSceneCount);
            Assert.AreEqual(originalActiveScene, SceneManager.GetActiveScene());
        }

        [Test]
        public void LoadPreview_OpensUnityTempBackupWithoutAssetsPrefix()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            Assert.IsNotNull(projectRoot);
            string backupDirectory = Path.Combine(projectRoot, "Temp", "__Backupscenes");
            Directory.CreateDirectory(backupDirectory);
            string backupFullPath = Path.Combine(backupDirectory, $"mcp-scene-safety-{Guid.NewGuid():N}.backup");
            File.Copy(Path.Combine(projectRoot, "Assets", "Scenes", "SampleScene.unity"), backupFullPath);
            _temporaryFiles.Add(backupFullPath);
            string backupProjectPath = backupFullPath.Substring(projectRoot.Length + 1).Replace('\\', '/');

            object loadResult = ManageScene.HandleCommand(new JObject
            {
                ["action"] = "load_preview",
                ["path"] = backupProjectPath
            });
            JObject loadResponse = loadResult as JObject ?? JObject.FromObject(loadResult);

            Assert.IsTrue(loadResponse.Value<bool>("success"), loadResponse.ToString());
            _previewLeaseId = loadResponse["data"]?["lease"]?.Value<string>("leaseId");
            Assert.IsNotEmpty(_previewLeaseId);
            Assert.AreEqual(
                backupProjectPath,
                loadResponse["data"]?["lease"]?.Value<string>("scenePath"));
            string temporaryCopyPath = loadResponse["data"]?["lease"]?.Value<string>("temporaryCopyPath");
            Assert.IsNotEmpty(temporaryCopyPath);
            Assert.IsTrue(File.Exists(Path.Combine(projectRoot, temporaryCopyPath)));

            object closeResult = ManageScene.HandleCommand(new JObject
            {
                ["action"] = "close_preview_scene",
                ["leaseId"] = _previewLeaseId
            });
            JObject closeResponse = closeResult as JObject ?? JObject.FromObject(closeResult);
            Assert.IsTrue(closeResponse.Value<bool>("success"), closeResponse.ToString());
            _previewLeaseId = null;
            Assert.IsFalse(File.Exists(Path.Combine(projectRoot, temporaryCopyPath)));
        }

        [Test]
        public void SceneCommandJournal_RecordsOnlySceneStateTransitions()
        {
            Assert.IsTrue(SceneCommandJournal.ShouldRecord(
                "manage_scene",
                new JObject { ["action"] = "load_preview" }));
            Assert.IsTrue(SceneCommandJournal.ShouldRecord(
                "manage_editor",
                new JObject { ["action"] = "play" }));
            Assert.IsFalse(SceneCommandJournal.ShouldRecord(
                "manage_scene",
                new JObject { ["action"] = "get_loaded_scenes" }));
            StringAssert.Contains(
                "Library",
                SceneCommandJournal.GetJournalPath().Replace('\\', '/'));
        }

        private Scene CreateAdditiveTestScene()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(activeScene.path))
            {
                string temporaryScenePath = AssetDatabase.GenerateUniqueAssetPath(
                    $"Assets/MCP Scene Safety Baseline {Guid.NewGuid():N}.unity");
                Assert.IsTrue(
                    EditorSceneManager.SaveScene(activeScene, temporaryScenePath),
                    "Failed to save the temporary baseline scene required for additive EditMode coverage.");
                _temporaryScenePaths.Add(temporaryScenePath);
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            _testScenes.Add(scene);
            return scene;
        }
    }
}
