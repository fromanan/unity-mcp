using System;
using System.IO;
using System.Linq;
using System.Text;
using MCPForUnity.Editor.Tools.Rendering;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class RenderingWorkflowTests
    {
        private const string TempRoot = "Assets/Temp/RenderingWorkflowTests";
        private string _materialPath;
        private string _colorProperty;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
            {
                AssetDatabase.CreateFolder("Assets", "Temp");
            }
            if (!AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.CreateFolder("Assets/Temp", "RenderingWorkflowTests");
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Assert.IsNotNull(shader);
            Material material = new(shader);
            _materialPath = $"{TempRoot}/Material_{Guid.NewGuid():N}.mat";
            _colorProperty = material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
            AssetDatabase.CreateAsset(material, _materialPath);
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TempRoot))
            {
                AssetDatabase.DeleteAsset(TempRoot);
            }
            CleanupEmptyParentFolders(TempRoot);
        }

        [Test]
        public void InspectMaterial_ReturnsExactIdentityAndDoesNotDirtyAsset()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(_materialPath);
            Assert.IsFalse(EditorUtility.IsDirty(material));
            JObject parameters = new()
            {
                ["action"] = "inspect_material",
                ["material_path"] = _materialPath,
                ["include_consumers"] = false,
            };

            JObject result = ToJObject(InspectRendering.HandleCommand(parameters));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(_materialPath, result["data"]?["asset"]?["path"]?.ToString());
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(_materialPath), result["data"]?["asset"]?["guid"]?.ToString());
            Assert.IsNotEmpty(result["data"]?["asset"]?["sha256"]?.ToString());
            Assert.Greater(result["data"]?["properties"]?.Count() ?? 0, 0);
            Assert.IsFalse(EditorUtility.IsDirty(material));
        }

        [Test]
        public void SampleMaterial_RendersCloneOnlyOverrideAndPreservesAssetsAndScene()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(_materialPath);
            Color sourceColor = material.GetColor(_colorProperty);
            string sourceSha = RenderingAssetUtility.ComputeSha256(_materialPath);
            bool materialDirtyBefore = EditorUtility.IsDirty(material);
            bool sceneDirtyBefore = UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty;
            string outputPath = $"Library/MCPForUnity/MaterialSamples/Tests/sample-{Guid.NewGuid():N}.png";
            string fullOutputPath = Path.GetFullPath(Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                outputPath.Replace('/', Path.DirectorySeparatorChar)));
            try
            {
                JObject result = ToJObject(InspectRendering.HandleCommand(new JObject
                {
                    ["action"] = "sample_material",
                    ["material_path"] = _materialPath,
                    ["profile"] = "pbr",
                    ["property_overrides"] = new JObject
                    {
                        [_colorProperty] = new JArray(0.15f, 0.35f, 0.75f, 1f),
                    },
                    ["max_resolution"] = 256,
                    ["warmup_frames"] = 0,
                    ["include_image"] = true,
                    ["output_path"] = outputPath,
                    ["cache_mode"] = "bypass",
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                Assert.AreEqual("unity-mcp/material-sample@1", result["data"]?["schema_version"]?.ToString());
                Assert.AreEqual("isolated_editor_material_sample", result["data"]?["proof"]?["level"]?.ToString());
                Assert.IsFalse(result["data"]?["context"]?["requires_scene_probe"]?.Value<bool>() ?? true);
                Assert.AreEqual("primary_temporary_clone_only", result["data"]?["property_overrides"]?["scope"]?.ToString());
                Assert.AreEqual(2, result["data"]?["preview"]?["panels"]?.Count());
                Assert.LessOrEqual(result["data"]?["preview"]?["width"]?.Value<int>() ?? int.MaxValue, 256);
                Assert.LessOrEqual(result["data"]?["preview"]?["height"]?.Value<int>() ?? int.MaxValue, 256);
                string encodedPng = result["data"]?["preview"]?["png_base64"]?.ToString();
                Assert.IsNotEmpty(encodedPng);
                Texture2D sampledImage = new(2, 2, TextureFormat.RGBA32, false, false);
                try
                {
                    Assert.IsTrue(sampledImage.LoadImage(Convert.FromBase64String(encodedPng)));
                    Assert.Greater(sampledImage.GetPixels32().Distinct().Count(), 8);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(sampledImage);
                }
                Assert.IsTrue(File.Exists(fullOutputPath), fullOutputPath);
                Assert.IsTrue(result["data"]?["restoration"]?["source_material_dirty_unchanged"]?.Value<bool>() ?? false);
                Assert.IsTrue(result["data"]?["restoration"]?["scene_dirty_unchanged"]?.Value<bool>() ?? false);
                Assert.IsTrue(result["data"]?["restoration"]?["render_texture_active_restored"]?.Value<bool>() ?? false);
                Assert.AreEqual(sourceColor, material.GetColor(_colorProperty));
                Assert.AreEqual(sourceSha, RenderingAssetUtility.ComputeSha256(_materialPath));
                Assert.AreEqual(materialDirtyBefore, EditorUtility.IsDirty(material));
                Assert.AreEqual(sceneDirtyBefore, UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty);
            }
            finally
            {
                if (File.Exists(fullOutputPath))
                {
                    File.Delete(fullOutputPath);
                }
            }
        }

        [Test]
        public void SampleMaterial_LocksComparisonViewsAndReusesDependencyCache()
        {
            JObject parameters = new()
            {
                ["action"] = "sample_material",
                ["material_path"] = _materialPath,
                ["compare_to_material_path"] = _materialPath,
                ["profile"] = "pbr",
                ["max_resolution"] = 256,
                ["warmup_frames"] = 0,
                ["include_image"] = false,
                ["cache_mode"] = "refresh",
            };
            string fullCachePath = null;
            try
            {
                JObject first = ToJObject(InspectRendering.HandleCommand(parameters));
                Assert.IsTrue(first.Value<bool>("success"), first.ToString());
                Assert.IsFalse(first["data"]?["cache"]?["hit"]?.Value<bool>() ?? true);
                Assert.AreEqual("same_views_side_by_side", first["data"]?["locked_manifest"]?["comparison_layout"]?.ToString());
                Assert.AreEqual(4, first["data"]?["preview"]?["panels"]?.Count());
                Assert.IsNull(first["data"]?["preview"]?["png_base64"]?.Value<string>());
                string cachePath = first["data"]?["cache"]?["path"]?.ToString();
                Assert.IsNotEmpty(cachePath);
                fullCachePath = Path.GetFullPath(Path.Combine(
                    Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                    cachePath.Replace('/', Path.DirectorySeparatorChar)));
                Assert.IsTrue(File.Exists(fullCachePath), fullCachePath);

                parameters["cache_mode"] = "use";
                JObject second = ToJObject(InspectRendering.HandleCommand(parameters));
                Assert.IsTrue(second.Value<bool>("success"), second.ToString());
                Assert.IsTrue(second["data"]?["cache"]?["hit"]?.Value<bool>() ?? false);
                Assert.AreEqual(
                    first["data"]?["preview"]?["output_sha256"]?.ToString(),
                    second["data"]?["preview"]?["output_sha256"]?.ToString());
            }
            finally
            {
                if (!string.IsNullOrEmpty(fullCachePath) && File.Exists(fullCachePath))
                {
                    File.Delete(fullCachePath);
                }
            }
        }

        [Test]
        public void SampleMaterial_RejectsUnknownOverrideAndEscapingOutputWithoutMutation()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(_materialPath);
            string sourceSha = RenderingAssetUtility.ComputeSha256(_materialPath);
            bool dirtyBefore = EditorUtility.IsDirty(material);

            JObject unknownOverride = ToJObject(InspectRendering.HandleCommand(new JObject
            {
                ["action"] = "sample_material",
                ["material_path"] = _materialPath,
                ["property_overrides"] = new JObject { ["_DefinitelyMissing"] = 1f },
                ["cache_mode"] = "bypass",
            }));
            Assert.IsFalse(unknownOverride.Value<bool>("success"), unknownOverride.ToString());
            StringAssert.Contains("has no material property", unknownOverride["error"]?.ToString());

            JObject escapingOutput = ToJObject(InspectRendering.HandleCommand(new JObject
            {
                ["action"] = "sample_material",
                ["material_path"] = _materialPath,
                ["output_path"] = "Library/MCPForUnity/MaterialSamples/../escaped.png",
            }));
            Assert.IsFalse(escapingOutput.Value<bool>("success"), escapingOutput.ToString());
            StringAssert.Contains("output_path", escapingOutput["error"]?.ToString());
            Assert.AreEqual(sourceSha, RenderingAssetUtility.ComputeSha256(_materialPath));
            Assert.AreEqual(dirtyBefore, EditorUtility.IsDirty(material));
        }

        [Test]
        public void InspectRenderTarget_ReportsMaterialSlotPropertyBlockAndPreservesSceneDirtyState()
        {
            GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gameObject.name = $"RenderingWorkflowCube_{Guid.NewGuid():N}";
            Renderer renderer = gameObject.GetComponent<Renderer>();
            renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(_materialPath);
            MaterialPropertyBlock block = new();
            block.SetColor(_colorProperty, Color.cyan);
            renderer.SetPropertyBlock(block, 0);
            bool dirtyBefore = gameObject.scene.isDirty;
            try
            {
                JObject result = ToJObject(InspectRendering.HandleCommand(new JObject
                {
                    ["action"] = "inspect_render_target",
                    ["target"] = gameObject.name,
                    ["page_size"] = 1000,
                    ["cursor"] = -12,
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                JToken record = result["data"]?["renderers"]?.First;
                Assert.IsNotNull(record);
                Assert.AreEqual(_materialPath, record?["materials"]?[0]?["material_path"]?.ToString());
                Assert.IsTrue(record?["materials"]?[0]?["has_property_block"]?.Value<bool>() ?? false);
                Assert.AreEqual(100, result["data"]?["page_size"]?.Value<int>());
                Assert.AreEqual(0, result["data"]?["cursor"]?.Value<int>());
                JToken colorOverride = record?["materials"]?[0]?["property_block_overrides"]
                    ?.FirstOrDefault(item => item?["name"]?.ToString() == _colorProperty);
                Assert.IsNotNull(colorOverride);
                Assert.AreEqual(1f, colorOverride?["value"]?["g"]?.Value<float>(), 0.0001f);
                Assert.AreEqual(dirtyBefore, gameObject.scene.isDirty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void MaterialAuthoring_DryRunDoesNotMutate_AndApplyIsIdempotent()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(_materialPath);
            Color initial = material.GetColor(_colorProperty);
            Color desired = new(0.2f, 0.4f, 0.6f, 1f);
            JArray operations = new()
            {
                new JObject
                {
                    ["op"] = "set_color",
                    ["property"] = _colorProperty,
                    ["value"] = new JArray(desired.r, desired.g, desired.b, desired.a),
                },
            };

            JObject dryRun = ToJObject(ManageRenderingAuthoring.HandleCommand(new JObject
            {
                ["asset_path"] = _materialPath,
                ["asset_kind"] = "material",
                ["operations"] = operations,
                ["dry_run"] = true,
            }));
            Assert.IsTrue(dryRun.Value<bool>("success"), dryRun.ToString());
            Assert.IsTrue(dryRun["data"]?["changed"]?.Value<bool>() ?? false);
            Assert.AreEqual(initial, material.GetColor(_colorProperty));
            Assert.IsFalse(EditorUtility.IsDirty(material));

            string sha = RenderingAssetUtility.ComputeSha256(_materialPath);
            JObject applied = ToJObject(ManageRenderingAuthoring.HandleCommand(new JObject
            {
                ["asset_path"] = _materialPath,
                ["asset_kind"] = "material",
                ["operations"] = operations,
                ["dry_run"] = false,
                ["expected_sha256"] = sha,
            }));
            Assert.IsTrue(applied.Value<bool>("success"), applied.ToString());
            Assert.IsTrue(applied["data"]?["changed"]?.Value<bool>() ?? false);
            material = AssetDatabase.LoadAssetAtPath<Material>(_materialPath);
            Assert.AreEqual(desired, material.GetColor(_colorProperty));

            string secondSha = RenderingAssetUtility.ComputeSha256(_materialPath);
            JObject idempotent = ToJObject(ManageRenderingAuthoring.HandleCommand(new JObject
            {
                ["asset_path"] = _materialPath,
                ["asset_kind"] = "material",
                ["operations"] = operations,
                ["dry_run"] = false,
                ["expected_sha256"] = secondSha,
            }));
            Assert.IsTrue(idempotent.Value<bool>("success"), idempotent.ToString());
            Assert.IsFalse(idempotent["data"]?["changed"]?.Value<bool>() ?? true);
        }

        [Test]
        public void ShaderGraphDocumentFile_ParsesConcatenatedJsonAndPreservesUnmodifiedDocumentBytes()
        {
            string temporaryPath = Path.GetTempFileName();
            string first = "{\r\n  \"m_Type\": \"GraphData\",\r\n  \"m_ObjectId\": \"root\",\r\n  \"m_SGVersion\": 3,\r\n  \"m_Guid\": \"11111111111111111111111111111111\",\r\n  \"m_Edges\": []\r\n}";
            string second = "{\r\n    \"m_Type\": \"TestSlot\",\r\n    \"m_ObjectId\": \"slot-1\",\r\n    \"m_Value\": 1.0\r\n}";
            byte[] content = Encoding.UTF8.GetBytes(first + "\r\n\r\n" + second + "\r\n");
            File.WriteAllBytes(temporaryPath, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(content).ToArray());
            try
            {
                ShaderGraphDocumentFile graph = ShaderGraphDocumentFile.Load(temporaryPath);
                Assert.AreEqual(2, graph.Documents.Count);
                Assert.IsTrue(graph.HasUtf8Bom);
                Assert.AreEqual("\r\n", graph.Newline);
                ShaderGraphDocument slot = graph.FindByObjectId("slot-1");
                Assert.IsNotNull(slot);
                JObject replacement = (JObject)slot.Value.DeepClone();
                replacement["m_Value"] = 2.0f;
                slot.Value = replacement;
                slot.IsModified = true;

                byte[] serializedBytes = graph.Serialize();
                Assert.That(serializedBytes.Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
                string serialized = Encoding.UTF8.GetString(serializedBytes, 3, serializedBytes.Length - 3);

                StringAssert.StartsWith(first, serialized);
                StringAssert.Contains("\"m_Value\": 2.0", serialized);
                StringAssert.Contains("\"m_SGVersion\": 3", serialized);
                StringAssert.Contains("11111111111111111111111111111111", serialized);
                Assert.AreEqual("root", graph.FindGraphRoot()?.ObjectId);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        [Test]
        public void ContractRegistry_EncodesFreshCanAndUrpMaskSemantics()
        {
            TextureSemanticContract packed = RenderingAssetUtility.ClassifyTextureContract(
                "Assets/FreshCan3D/T_Stone_N_AO_R.png",
                null);
            Assert.AreEqual("freshcan_n_ao_r", packed.Name);
            Assert.AreEqual("tangent_normal_x", packed.Channels["r"]);
            Assert.AreEqual("ambient_occlusion", packed.Channels["b"]);
            Assert.AreEqual("roughness", packed.Channels["a"]);
            Assert.AreEqual(false, packed.ExpectedSrgb);

            TextureSemanticContract mask = RenderingAssetUtility.ClassifyTextureContract(
                "Assets/Shaders/Generated/GeneratedMasks/FreshCan/T_Stone_Mask.png",
                null);
            Assert.AreEqual("urp_mask", mask.Name);
            Assert.AreEqual("metallic", mask.Channels["r"]);
            Assert.AreEqual("ambient_occlusion", mask.Channels["g"]);
            Assert.AreEqual("smoothness", mask.Channels["a"]);
        }

        [Test]
        public void PackagePathResolution_ResolvesTheInstalledPackagePayload()
        {
            const string packageAsset = "Packages/com.coplaydev.unity-mcp/package.json";

            string fullPath = RenderingAssetUtility.GetFullPath(packageAsset);

            Assert.IsNotNull(fullPath);
            Assert.IsTrue(File.Exists(fullPath), fullPath);
            Assert.IsNotEmpty(RenderingAssetUtility.ComputeSha256(packageAsset));
        }

        [Test]
        public void InspectTexture_ReturnsExactImporterAndBoundedSampleWithoutDirtying()
        {
            string texturePath = CreateTextureAsset("TextureExact", false);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            string sourceShaBefore = RenderingAssetUtility.ComputeSha256(texturePath);
            string importerShaBefore = RenderingAssetUtility.ComputeAuthoringSha256(texturePath, "texture_importer");

            JObject result = ToJObject(InspectRendering.HandleCommand(new JObject
            {
                ["action"] = "inspect_texture",
                ["texture_path"] = texturePath,
                ["semantic_contract"] = "freshcan_n_ao_r",
                ["sample_size"] = 2,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(texturePath, result["data"]?["asset"]?["path"]?.ToString());
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(texturePath), result["data"]?["asset"]?["guid"]?.ToString());
            Assert.AreEqual(4, result["data"]?["asset"]?["source_dimensions"]?["width"]?.Value<int>());
            Assert.AreEqual(4, result["data"]?["runtime"]?["width"]?.Value<int>());
            Assert.AreEqual(false, result["data"]?["importer"]?["srgb"]?.Value<bool>());
            Assert.AreEqual(4, result["data"]?["sampling"]?["sample_width"]?.Value<int>());
            Assert.AreEqual(16, result["data"]?["sampling"]?["sample_count"]?.Value<int>());
            Assert.AreEqual("freshcan_n_ao_r", result["data"]?["semantic_contract"]?["name"]?.ToString());
            Assert.AreEqual(4, result["data"]?["sampling"]?["channel_thumbnails"]?.Children<JProperty>().Count());
            Assert.AreEqual(sourceShaBefore, RenderingAssetUtility.ComputeSha256(texturePath));
            Assert.AreEqual(importerShaBefore, RenderingAssetUtility.ComputeAuthoringSha256(texturePath, "texture_importer"));
            Assert.IsFalse(EditorUtility.IsDirty(texture));
        }

        [Test]
        public void TextureImporterAuthoring_UsesMetaPreconditionAndIsIdempotent()
        {
            string texturePath = CreateTextureAsset("TextureImporter", true);
            string sourceSha = RenderingAssetUtility.ComputeSha256(texturePath);
            string staleMetaSha = RenderingAssetUtility.ComputeAuthoringSha256(texturePath, "texture_importer");
            JArray operations = new()
            {
                new JObject { ["op"] = "set", ["field"] = "srgb", ["value"] = false },
            };

            JObject dryRun = ToJObject(ManageRenderingAuthoring.HandleCommand(new JObject
            {
                ["asset_path"] = texturePath,
                ["asset_kind"] = "texture_importer",
                ["operations"] = operations,
                ["dry_run"] = true,
            }));
            Assert.IsTrue(dryRun.Value<bool>("success"), dryRun.ToString());
            Assert.AreEqual(texturePath + ".meta", dryRun["data"]?["apply_precondition_path"]?.ToString());
            Assert.AreEqual(staleMetaSha, dryRun["data"]?["apply_precondition_sha256"]?.ToString());
            Assert.AreEqual(sourceSha, RenderingAssetUtility.ComputeSha256(texturePath));

            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            Assert.IsNotNull(importer);
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            Assert.AreNotEqual(staleMetaSha, RenderingAssetUtility.ComputeAuthoringSha256(texturePath, "texture_importer"));

            JObject staleApply = ToJObject(ManageRenderingAuthoring.HandleCommand(new JObject
            {
                ["asset_path"] = texturePath,
                ["asset_kind"] = "texture_importer",
                ["operations"] = operations,
                ["dry_run"] = false,
                ["expected_sha256"] = staleMetaSha,
            }));
            Assert.IsFalse(staleApply.Value<bool>("success"), staleApply.ToString());
            StringAssert.Contains(
                "SHA-256 precondition failed",
                staleApply["error"]?.ToString() ?? staleApply["code"]?.ToString());

            string currentMetaSha = RenderingAssetUtility.ComputeAuthoringSha256(texturePath, "texture_importer");
            JObject applied = ToJObject(ManageRenderingAuthoring.HandleCommand(new JObject
            {
                ["asset_path"] = texturePath,
                ["asset_kind"] = "texture_importer",
                ["operations"] = operations,
                ["dry_run"] = false,
                ["expected_sha256"] = currentMetaSha,
            }));
            Assert.IsTrue(applied.Value<bool>("success"), applied.ToString());
            Assert.IsTrue(applied["data"]?["changed"]?.Value<bool>() ?? false);
            Assert.AreEqual(sourceSha, RenderingAssetUtility.ComputeSha256(texturePath));

            string secondMetaSha = RenderingAssetUtility.ComputeAuthoringSha256(texturePath, "texture_importer");
            JObject idempotent = ToJObject(ManageRenderingAuthoring.HandleCommand(new JObject
            {
                ["asset_path"] = texturePath,
                ["asset_kind"] = "texture_importer",
                ["operations"] = operations,
                ["dry_run"] = false,
                ["expected_sha256"] = secondMetaSha,
            }));
            Assert.IsTrue(idempotent.Value<bool>("success"), idempotent.ToString());
            Assert.IsFalse(idempotent["data"]?["changed"]?.Value<bool>() ?? true);
        }

        [Test]
        public void InspectShaderGraph_ReportsVersionSlotsAndInertProperties()
        {
            string graphPath = $"{TempRoot}/Synthetic_{Guid.NewGuid():N}.shadergraph";
            string root = "{\n  \"m_Type\": \"UnityEditor.ShaderGraph.GraphData\",\n  \"m_ObjectId\": \"root\",\n  \"m_SGVersion\": 3,\n  \"m_Edges\": [],\n  \"m_Nodes\": [{\"m_Id\": \"node\"}],\n  \"m_Properties\": [{\"m_Id\": \"property\"}],\n  \"m_ActiveTargets\": []\n}";
            string property = "{\n  \"m_Type\": \"UnityEditor.ShaderGraph.Vector1ShaderProperty\",\n  \"m_ObjectId\": \"property\",\n  \"m_ReferenceName\": \"_Inert\",\n  \"m_DisplayName\": \"Inert\",\n  \"m_Value\": 0.5\n}";
            string node = "{\n  \"m_Type\": \"UnityEditor.ShaderGraph.PropertyNode\",\n  \"m_ObjectId\": \"node\",\n  \"m_Property\": {\"m_Id\": \"property\"},\n  \"m_Slots\": [{\"m_Id\": \"slot\"}]\n}";
            string slot = "{\n  \"m_Type\": \"UnityEditor.ShaderGraph.Vector1MaterialSlot\",\n  \"m_ObjectId\": \"slot\",\n  \"m_Id\": 0,\n  \"m_DisplayName\": \"Out\",\n  \"m_Value\": 0.0\n}";
            File.WriteAllText(RenderingAssetUtility.GetFullPath(graphPath), string.Join("\n\n", root, property, node, slot) + "\n");

            JObject result = ToJObject(InspectRendering.HandleCommand(new JObject
            {
                ["action"] = "inspect_shader_graph",
                ["shader_path"] = graphPath,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(3, result["data"]?["graph"]?["graph_version"]?.Value<int>());
            Assert.AreEqual(1, result["data"]?["graph"]?["slots"]?.Count());
            Assert.AreEqual("_Inert", result["data"]?["graph"]?["inert_property_reference_names"]?[0]?.ToString());
            JToken trace = result["data"]?["graph"]?["property_output_traces"]?.First;
            Assert.AreEqual(false, trace?["reaches_output"]?.Value<bool>());
            Assert.AreEqual("inert", trace?["status"]?.ToString());
        }

        [Test]
        public void ValidateRenderContract_StrictUnknownTextureFailsClosed()
        {
            string texturePath = CreateTextureAsset("UnknownSemantic", true);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(_materialPath);
            string textureProperty = material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
            material.SetTexture(textureProperty, AssetDatabase.LoadAssetAtPath<Texture>(texturePath));
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);

            JObject result = ToJObject(InspectRendering.HandleCommand(new JObject
            {
                ["action"] = "validate_render_contract",
                ["material_path"] = _materialPath,
                ["strict"] = true,
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsFalse(result["data"]?["passed"]?.Value<bool>() ?? true);
            Assert.Greater(result["data"]?["unknown_count"]?.Value<int>() ?? 0, 0);
            Assert.Greater(result["data"]?["failure_count"]?.Value<int>() ?? 0, 0);
        }

        [Test]
        public void RenderProbe_LocksManifestAndRestoresCameraQualityAndRenderState()
        {
            GameObject cameraObject = new($"RenderingProbeCamera_{Guid.NewGuid():N}");
            Camera camera = cameraObject.AddComponent<Camera>();
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 0f, -4f), Quaternion.identity);
            GameObject target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            target.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(_materialPath);
            string outputPath = $"Library/MCPForUnity/RenderProbes/Tests/rendering-workflow-{Guid.NewGuid():N}.png";
            string fullOutputPath = Path.GetFullPath(Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                outputPath.Replace('/', Path.DirectorySeparatorChar)));
            int qualityBefore = QualitySettings.GetQualityLevel();
            RenderTexture targetBefore = camera.targetTexture;
            RenderTexture activeBefore = RenderTexture.active;
            bool wireframeBefore = GL.wireframe;
            bool sceneDirtyBefore = cameraObject.scene.isDirty;
            try
            {
                JObject result = ToJObject(InspectRendering.HandleCommand(new JObject
                {
                    ["action"] = "render_probe",
                    ["camera"] = cameraObject.name,
                    ["scope"] = "scene",
                    ["output_path"] = outputPath,
                    ["width"] = 64,
                    ["height"] = 64,
                    ["channel"] = "color",
                    ["warmup_frames"] = 0,
                    ["quality_level"] = qualityBefore,
                }));

                Assert.IsTrue(result.Value<bool>("success"), result.ToString());
                Assert.IsTrue(File.Exists(fullOutputPath), fullOutputPath);
                Assert.AreEqual(64, result["data"]?["width"]?.Value<int>());
                Assert.AreEqual(64, result["data"]?["height"]?.Value<int>());
                Assert.IsNotEmpty(result["data"]?["output_sha256"]?.ToString());
                Assert.IsTrue(result["data"]?["restoration"]?["camera_target_texture_restored"]?.Value<bool>() ?? false);
                Assert.IsTrue(result["data"]?["restoration"]?["render_texture_active_restored"]?.Value<bool>() ?? false);
                Assert.IsTrue(result["data"]?["restoration"]?["quality_level_restored"]?.Value<bool>() ?? false);
                Assert.IsTrue(result["data"]?["restoration"]?["wireframe_restored"]?.Value<bool>() ?? false);
                Assert.IsTrue(result["data"]?["restoration"]?["camera_transform_restored"]?.Value<bool>() ?? false);
                Assert.IsTrue(result["data"]?["restoration"]?["camera_projection_restored"]?.Value<bool>() ?? false);
                Assert.AreSame(targetBefore, camera.targetTexture);
                Assert.AreSame(activeBefore, RenderTexture.active);
                Assert.AreEqual(qualityBefore, QualitySettings.GetQualityLevel());
                Assert.AreEqual(wireframeBefore, GL.wireframe);
                Assert.AreEqual(sceneDirtyBefore, cameraObject.scene.isDirty);
            }
            finally
            {
                if (File.Exists(fullOutputPath))
                {
                    File.Delete(fullOutputPath);
                }
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        private static string CreateTextureAsset(string name, bool srgb)
        {
            string texturePath = $"{TempRoot}/{name}_{Guid.NewGuid():N}.png";
            Texture2D source = new(4, 4, TextureFormat.RGBA32, false, true);
            Color[] pixels = Enumerable.Range(0, 16)
                .Select(index => new Color(
                    (index % 4) / 3f,
                    (index / 4) / 3f,
                    0.5f,
                    index % 2 == 0 ? 1f : 0.25f))
                .ToArray();
            source.SetPixels(pixels);
            source.Apply(false, false);
            File.WriteAllBytes(RenderingAssetUtility.GetFullPath(texturePath), source.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(source);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            Assert.IsNotNull(importer);
            importer.sRGBTexture = srgb;
            importer.mipmapEnabled = true;
            importer.textureType = TextureImporterType.Default;
            importer.SaveAndReimport();
            return texturePath;
        }
    }
}
