using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools.Profiler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace MCPForUnity.Editor.Tools.Rendering
{
    [McpForUnityTool(
        "inspect_rendering",
        AutoRegister = true,
        Group = "rendering_inspect",
        Description = "Bounded read-only rendering inspection, contract validation, deterministic render probes, and target profiling.")]
    public static class InspectRendering
    {
        private sealed class GraphAnalysis
        {
            public ShaderGraphDocumentFile File { get; set; }
            public ShaderGraphDocument Root { get; set; }
            public List<object> Properties { get; set; } = new();
            public List<object> Nodes { get; set; } = new();
            public List<object> Slots { get; set; } = new();
            public List<object> Edges { get; set; } = new();
            public List<object> Subgraphs { get; set; } = new();
            public List<object> Traces { get; set; } = new();
            public HashSet<string> InertReferenceNames { get; set; } = new(StringComparer.Ordinal);
            public List<object> Targets { get; set; } = new();
            public object GraphVersion { get; set; }
        }

        private sealed class MaterialSampleView
        {
            public string Name { get; set; }
            public string MeshKind { get; set; }
            public Vector3 CameraPosition { get; set; }
            public Vector3 CameraEuler { get; set; }
            public Vector3 ObjectScale { get; set; }
            public Vector3 ObjectEuler { get; set; }
            public Color Background { get; set; }
            public Vector3 KeyLightEuler { get; set; }
            public float KeyLightIntensity { get; set; }
            public float BackLightIntensity { get; set; }
            public Color Ambient { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            ToolParams parameters = new(@params);
            string action = parameters.Get("action")?.Trim().ToLowerInvariant();
            try
            {
                switch (action)
                {
                    case "inspect_render_target":
                    {
                        return InspectRenderTarget(parameters);
                    }
                    case "inspect_material":
                    {
                        return InspectMaterial(parameters);
                    }
                    case "inspect_texture":
                    {
                        return InspectTexture(parameters);
                    }
                    case "inspect_shader_graph":
                    {
                        return InspectShaderGraph(parameters);
                    }
                    case "validate_render_contract":
                    {
                        return ValidateRenderContract(parameters);
                    }
                    case "sample_material":
                    {
                        return SampleMaterial(parameters);
                    }
                    case "render_probe":
                    {
                        return RenderProbe(parameters);
                    }
                    case "profile_render_target":
                    {
                        return ProfileRenderTarget(parameters);
                    }
                    case "ping":
                    {
                        return new SuccessResponse("pong", new
                        {
                            tool = "inspect_rendering",
                            contract_registry = RenderingAssetUtility.ContractRegistryVersion,
                        });
                    }
                    default:
                    {
                        return new ErrorResponse(
                            "Unknown action. Supported: inspect_render_target, inspect_material, "
                            + "inspect_texture, inspect_shader_graph, validate_render_contract, "
                            + "sample_material, render_probe, profile_render_target, ping.");
                    }
                }
            }
            catch (Exception exception)
            {
                return new ErrorResponse($"Rendering inspection failed: {exception.Message}", new
                {
                    exception = exception.GetType().FullName,
                });
            }
        }

        private static object InspectRenderTarget(ToolParams parameters)
        {
            string target = parameters.Get("target");
            GameObject gameObject = RenderingAssetUtility.ResolveGameObject(target);
            if (gameObject == null)
            {
                return new ErrorResponse($"Could not resolve scene GameObject '{target}'.");
            }

            bool includeChildren = parameters.GetBool("include_children", true);
            bool includeInactive = parameters.GetBool("include_inactive", true);
            int pageSize = ClampPageSize(parameters.GetInt("page_size") ?? 25);
            int cursor = Math.Max(0, parameters.GetInt("cursor") ?? 0);
            Renderer[] renderers = includeChildren
                ? gameObject.GetComponentsInChildren<Renderer>(includeInactive)
                : gameObject.GetComponents<Renderer>();
            List<Renderer> ordered = renderers
                .Where(renderer => renderer != null)
                .OrderBy(renderer => RenderingAssetUtility.GetHierarchyPath(renderer.gameObject), StringComparer.Ordinal)
                .ThenBy(renderer => renderer.GetInstanceID())
                .ToList();
            int end = Math.Min(cursor + pageSize, ordered.Count);
            List<object> records = new();
            for (int index = cursor; index < end; index++)
            {
                records.Add(BuildRendererRecord(ordered[index]));
            }

            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            RenderingOwnershipInfo ownership = RenderingAssetUtility.ClassifyOwnership(assetPath);
            Dictionary<string, object> data = new()
            {
                ["schema_version"] = "unity-mcp/render-target@1",
                ["target"] = new
                {
                    name = gameObject.name,
                    hierarchy_path = RenderingAssetUtility.GetHierarchyPath(gameObject),
                    instance_id = gameObject.GetInstanceID(),
                    active_self = gameObject.activeSelf,
                    active_in_hierarchy = gameObject.activeInHierarchy,
                    scene_path = gameObject.scene.path,
                    prefab_asset_path = assetPath,
                    ownership,
                },
                ["renderers"] = records,
                ["renderer_count"] = ordered.Count,
                ["cursor"] = cursor,
                ["page_size"] = pageSize,
                ["proof"] = new
                {
                    level = "live_editor_scene",
                    includes = new[]
                    {
                        "renderer ownership",
                        "shared material slots",
                        "material property blocks",
                        "mesh/submesh closure",
                        "LOD membership",
                    },
                    excludes = new[] { "Player runtime", "target GPU", "uncaptured Frame Debugger events" },
                },
            };
            if (end < ordered.Count)
            {
                data["next_cursor"] = end;
            }
            return new SuccessResponse($"Inspected {records.Count} of {ordered.Count} renderers.", data);
        }

        private static object InspectMaterial(ToolParams parameters)
        {
            string materialPath = RenderingAssetUtility.NormalizeAssetPath(parameters.Get("material_path"));
            if (!RenderingAssetUtility.IsExactAssetPath(materialPath))
            {
                return new ErrorResponse("material_path must be an exact path under Assets/ or Packages/.");
            }
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                return new ErrorResponse($"Could not load Material at '{materialPath}'.");
            }
            return new SuccessResponse($"Inspected material '{material.name}'.", BuildMaterialRecord(
                material,
                materialPath,
                parameters.GetBool("include_consumers", true),
                ClampPageSize(parameters.GetInt("page_size") ?? 25),
                Math.Max(0, parameters.GetInt("cursor") ?? 0)));
        }

        private static object InspectTexture(ToolParams parameters)
        {
            string texturePath = RenderingAssetUtility.NormalizeAssetPath(parameters.Get("texture_path"));
            if (!RenderingAssetUtility.IsExactAssetPath(texturePath))
            {
                return new ErrorResponse("texture_path must be an exact path under Assets/ or Packages/.");
            }
            Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null)
            {
                return new ErrorResponse($"Could not load Texture at '{texturePath}'.");
            }

            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            TextureSemanticContract contract = RenderingAssetUtility.ClassifyTextureContract(
                texturePath,
                parameters.Get("semantic_contract"));
            int sampleSize = Math.Max(16, Math.Min(256, parameters.GetInt("sample_size") ?? 128));
            object sampleEvidence = SampleTexture(texture, sampleSize, contract);
            int sourceWidth = texture.width;
            int sourceHeight = texture.height;
            if (importer != null)
            {
                importer.GetSourceTextureWidthAndHeight(out sourceWidth, out sourceHeight);
            }

            List<object> platformSettings = new();
            if (importer != null)
            {
                foreach (string platform in new[] { "DefaultTexturePlatform", "Standalone", "Android", "iPhone", "WebGL" })
                {
                    TextureImporterPlatformSettings settings = importer.GetPlatformTextureSettings(platform);
                    platformSettings.Add(new
                    {
                        platform,
                        overridden = settings.overridden,
                        max_texture_size = settings.maxTextureSize,
                        resize_algorithm = settings.resizeAlgorithm.ToString(),
                        format = settings.format.ToString(),
                        compression_quality = settings.compressionQuality,
                        allows_alpha_splitting = settings.allowsAlphaSplitting,
                    });
                }
            }

            object importerRecord = importer == null
                ? new { available = false }
                : new
                {
                    available = true,
                    importer_type = importer.textureType.ToString(),
                    importer_shape = importer.textureShape.ToString(),
                    srgb = importer.sRGBTexture,
                    alpha_source = importer.alphaSource.ToString(),
                    alpha_is_transparency = importer.alphaIsTransparency,
                    mipmaps = importer.mipmapEnabled,
                    preserve_alpha_coverage = importer.mipMapsPreserveCoverage,
                    alpha_test_reference = importer.alphaTestReferenceValue,
                    mip_filter = importer.mipmapFilter.ToString(),
                    streaming_mipmaps = importer.streamingMipmaps,
                    wrap_u = importer.wrapModeU.ToString(),
                    wrap_v = importer.wrapModeV.ToString(),
                    wrap_w = importer.wrapModeW.ToString(),
                    filter_mode = importer.filterMode.ToString(),
                    anisotropic_level = importer.anisoLevel,
                    compression = importer.textureCompression.ToString(),
                    compression_quality = importer.compressionQuality,
                    crunched_compression = importer.crunchedCompression,
                    readable = importer.isReadable,
                    npot_scale = importer.npotScale.ToString(),
                    platform_settings = platformSettings,
                };

            Texture2D texture2D = texture as Texture2D;
            object runtimeRecord = new
            {
                width = texture.width,
                height = texture.height,
                dimension = texture.dimension.ToString(),
                graphics_format = texture.graphicsFormat.ToString(),
                texture_format = texture2D != null ? texture2D.format.ToString() : null,
                mip_count = texture.mipmapCount,
                runtime_memory_bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture),
                filter_mode = texture.filterMode.ToString(),
                wrap_mode = texture.wrapMode.ToString(),
                anisotropic_level = texture.anisoLevel,
            };

            bool? srgbMatches = importer == null || !contract.ExpectedSrgb.HasValue
                ? null
                : importer.sRGBTexture == contract.ExpectedSrgb.Value;
            bool? typeMatches = importer == null || string.IsNullOrEmpty(contract.ExpectedImporterType)
                ? null
                : string.Equals(importer.textureType.ToString(), contract.ExpectedImporterType, StringComparison.OrdinalIgnoreCase);
            bool? mipMatches = importer == null || !contract.ExpectedMipmaps.HasValue
                ? null
                : importer.mipmapEnabled == contract.ExpectedMipmaps.Value;

            return new SuccessResponse($"Inspected texture '{texture.name}'.", new
            {
                schema_version = "unity-mcp/texture-inspection@1",
                asset = new
                {
                    path = texturePath,
                    guid = AssetDatabase.AssetPathToGUID(texturePath),
                    name = texture.name,
                    source_dimensions = new { width = sourceWidth, height = sourceHeight },
                    sha256 = RenderingAssetUtility.ComputeSha256(texturePath),
                    ownership = RenderingAssetUtility.ClassifyOwnership(texturePath),
                },
                importer = importerRecord,
                runtime = runtimeRecord,
                sampling = sampleEvidence,
                semantic_contract = new
                {
                    registry_version = RenderingAssetUtility.ContractRegistryVersion,
                    name = contract.Name,
                    source = contract.Source,
                    expected_srgb = contract.ExpectedSrgb,
                    expected_importer_type = contract.ExpectedImporterType,
                    expected_mipmaps = contract.ExpectedMipmaps,
                    channels = contract.Channels,
                    known = contract.IsKnown,
                    validation = new
                    {
                        srgb_matches = srgbMatches,
                        importer_type_matches = typeMatches,
                        mipmaps_match = mipMatches,
                    },
                },
                proof = new
                {
                    level = "exact_asset_editor_runtime_sample",
                    bounded_sample = sampleSize,
                    source_pixels_are_not_loaded_project_wide = true,
                },
            });
        }

        private static object InspectShaderGraph(ToolParams parameters)
        {
            string shaderPath = RenderingAssetUtility.NormalizeAssetPath(parameters.Get("shader_path"));
            if (!RenderingAssetUtility.IsExactAssetPath(shaderPath))
            {
                return new ErrorResponse("shader_path must be an exact path under Assets/ or Packages/.");
            }
            string fullPath = RenderingAssetUtility.GetFullPath(shaderPath);
            if (fullPath == null || !File.Exists(fullPath))
            {
                return new ErrorResponse($"Shader asset does not exist at '{shaderPath}'.");
            }

            string extension = Path.GetExtension(shaderPath).ToLowerInvariant();
            bool isGraph = extension == ".shadergraph" || extension == ".shadersubgraph";
            int pageSize = ClampPageSize(parameters.GetInt("page_size") ?? 50);
            int cursor = Math.Max(0, parameters.GetInt("cursor") ?? 0);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            List<object> messages = GetShaderMessages(shader);
            List<object> passes = GetShaderPasses(shader);
            List<string> keywords = shader == null
                ? new List<string>()
                : shader.keywordSpace.keywordNames.OrderBy(value => value, StringComparer.Ordinal).ToList();

            if (!isGraph)
            {
                return new SuccessResponse($"Inspected ShaderLab shader '{shaderPath}'.", new
                {
                    schema_version = "unity-mcp/shader-inspection@1",
                    asset = BuildShaderAssetIdentity(shaderPath, shader, "ShaderLab"),
                    passes,
                    keywords,
                    compiler_messages = messages,
                    graph = (object)null,
                    proof = new { level = "exact_shader_asset_and_imported_shader" },
                });
            }

            GraphAnalysis analysis = AnalyzeShaderGraph(shaderPath);
            IReadOnlyList<ShaderGraphDocument> documents = analysis.File.Documents;
            int end = Math.Min(cursor + pageSize, documents.Count);
            List<object> documentSummaries = new();
            for (int index = cursor; index < end; index++)
            {
                ShaderGraphDocument document = documents[index];
                documentSummaries.Add(new
                {
                    index,
                    object_id = document.ObjectId,
                    type = document.TypeName,
                    name = document.Value?["m_Name"]?.ToString()
                        ?? document.Value?["m_DisplayName"]?.ToString()
                        ?? document.Value?["m_ReferenceName"]?.ToString(),
                    slot_count = (document.Value?["m_Slots"] as JArray)?.Count,
                });
            }

            Dictionary<string, object> graphData = new()
            {
                ["document_count"] = documents.Count,
                ["documents"] = documentSummaries,
                ["cursor"] = cursor,
                ["page_size"] = pageSize,
                ["targets"] = analysis.Targets,
                ["graph_version"] = analysis.GraphVersion,
                ["properties"] = analysis.Properties,
                ["nodes"] = analysis.Nodes,
                ["slots"] = analysis.Slots,
                ["edges"] = analysis.Edges,
                ["subgraphs"] = analysis.Subgraphs,
                ["property_output_traces"] = analysis.Traces,
                ["inert_property_reference_names"] = analysis.InertReferenceNames.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            };
            if (end < documents.Count)
            {
                graphData["next_cursor"] = end;
            }

            return new SuccessResponse($"Inspected Shader Graph '{shaderPath}'.", new
            {
                schema_version = "unity-mcp/shader-graph-inspection@1",
                asset = BuildShaderAssetIdentity(shaderPath, shader, extension == ".shadersubgraph" ? "ShaderSubGraph" : "ShaderGraph"),
                graph = graphData,
                passes,
                keywords,
                compiler_messages = messages,
                proof = new
                {
                    level = "exact_graph_documents_and_imported_shader",
                    parser = "multi_document_json_byte_preserving",
                    limitations = new[]
                    {
                        "Reachability proves serialized graph routing, not numerical visual quality.",
                        "Compiled messages prove the current Editor import, not Player/target GPU execution.",
                    },
                },
            });
        }

        private static object ValidateRenderContract(ToolParams parameters)
        {
            string materialPath = RenderingAssetUtility.NormalizeAssetPath(parameters.Get("material_path"));
            string target = parameters.Get("target");
            bool strict = parameters.GetBool("strict", true);
            JObject callerContracts = parameters.GetRaw("contracts") as JObject;
            List<Material> materials = new();
            List<object> checks = new();
            List<(string context, string property, Texture texture)> propertyBlockTextures = new();
            HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(materialPath))
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    return new ErrorResponse($"Could not load Material at '{materialPath}'.");
                }
                materials.Add(material);
                seenPaths.Add(materialPath);
            }

            if (!string.IsNullOrEmpty(target))
            {
                GameObject gameObject = RenderingAssetUtility.ResolveGameObject(target);
                if (gameObject == null)
                {
                    return new ErrorResponse($"Could not resolve scene GameObject '{target}'.");
                }
                Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer renderer in renderers)
                {
                    MaterialPropertyBlock rendererBlock = new();
                    renderer.GetPropertyBlock(rendererBlock);
                    for (int slot = 0; slot < renderer.sharedMaterials.Length; slot++)
                    {
                        Material material = renderer.sharedMaterials[slot];
                        if (material == null)
                        {
                            checks.Add(ContractCheck(
                                "renderer_material_binding",
                                "error",
                                "fail",
                                $"{RenderingAssetUtility.GetHierarchyPath(renderer.gameObject)} slot {slot} is null.",
                                "live_editor_scene"));
                            continue;
                        }
                        MaterialPropertyBlock slotBlock = new();
                        renderer.GetPropertyBlock(slotBlock, slot);
                        Shader slotShader = material.shader;
                        int propertyCount = slotShader == null ? 0 : slotShader.GetPropertyCount();
                        for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                        {
                            if (slotShader.GetPropertyType(propertyIndex) != ShaderPropertyType.Texture)
                            {
                                continue;
                            }
                            string propertyName = slotShader.GetPropertyName(propertyIndex);
                            MaterialPropertyBlock effectiveBlock = slotBlock.HasProperty(propertyName)
                                ? slotBlock
                                : rendererBlock.HasProperty(propertyName) ? rendererBlock : null;
                            if (effectiveBlock == null)
                            {
                                continue;
                            }
                            string context = $"{RenderingAssetUtility.GetHierarchyPath(renderer.gameObject)} slot {slot}";
                            Texture overrideTexture = effectiveBlock.GetTexture(propertyName);
                            propertyBlockTextures.Add((context, propertyName, overrideTexture));
                            checks.Add(ContractCheck(
                                "material_property_block_override",
                                overrideTexture == null ? "error" : "info",
                                overrideTexture == null ? "fail" : "pass",
                                overrideTexture == null
                                    ? $"{context} overrides {propertyName} with a null texture."
                                    : $"{context} overrides {propertyName} with {AssetDatabase.GetAssetPath(overrideTexture)}.",
                                "live_editor_scene"));
                        }
                        string path = AssetDatabase.GetAssetPath(material);
                        if (seenPaths.Add(path))
                        {
                            materials.Add(material);
                        }
                    }
                }
            }

            if (materials.Count == 0)
            {
                return new ErrorResponse("Provide material_path, target, or both.");
            }

            int unknownCount = 0;
            foreach ((string context, string property, Texture texture) in propertyBlockTextures)
            {
                if (texture == null)
                {
                    continue;
                }
                string requestedContract = callerContracts?[property]?["semantic_contract"]?.ToString();
                AddTextureContractCheck(
                    context,
                    property,
                    texture,
                    requestedContract,
                    strict,
                    checks,
                    ref unknownCount,
                    "material_property_block");
            }
            foreach (Material material in materials)
            {
                string path = AssetDatabase.GetAssetPath(material);
                RenderingOwnershipInfo ownership = RenderingAssetUtility.ClassifyOwnership(path);
                checks.Add(ContractCheck(
                    "asset_ownership",
                    ownership.IsVendor || ownership.IsGenerated ? "warning" : "info",
                    "pass",
                    $"{path}: {ownership.Owner}/{ownership.AssetClass}. {ownership.Reason}",
                    "path_registry"));

                if (material.shader == null)
                {
                    checks.Add(ContractCheck("shader_binding", "error", "fail", $"{path} has no shader.", "exact_material"));
                    continue;
                }

                string shaderPath = AssetDatabase.GetAssetPath(material.shader);
                checks.Add(ContractCheck(
                    "shader_binding",
                    "info",
                    "pass",
                    $"{path} -> {material.shader.name} ({shaderPath}).",
                    "exact_material"));

                HashSet<string> inertReferences = new(StringComparer.Ordinal);
                string extension = Path.GetExtension(shaderPath).ToLowerInvariant();
                if (extension == ".shadergraph" || extension == ".shadersubgraph")
                {
                    try
                    {
                        GraphAnalysis graph = AnalyzeShaderGraph(shaderPath);
                        inertReferences = graph.InertReferenceNames;
                    }
                    catch (Exception exception)
                    {
                        unknownCount++;
                        checks.Add(ContractCheck(
                            "shader_graph_route",
                            strict ? "error" : "warning",
                            strict ? "fail" : "unknown",
                            $"Could not prove graph routing for {shaderPath}: {exception.Message}",
                            "unknown"));
                    }
                }

                int propertyCount = material.shader.GetPropertyCount();
                for (int index = 0; index < propertyCount; index++)
                {
                    if (material.shader.GetPropertyType(index) != ShaderPropertyType.Texture)
                    {
                        continue;
                    }
                    string propertyName = material.shader.GetPropertyName(index);
                    Texture texture = material.GetTexture(propertyName);
                    if (texture == null)
                    {
                        bool required = callerContracts?[propertyName]?["required"]?.Value<bool>() ?? false;
                        if (required)
                        {
                            checks.Add(ContractCheck(
                                "required_texture_binding",
                                "error",
                                "fail",
                                $"{path} property {propertyName} is required but unbound.",
                                "caller_contract"));
                        }
                        continue;
                    }

                    string requestedContract = callerContracts?[propertyName]?["semantic_contract"]?.ToString();
                    AddTextureContractCheck(
                        path,
                        propertyName,
                        texture,
                        requestedContract,
                        strict,
                        checks,
                        ref unknownCount,
                        "material_asset");

                    if (inertReferences.Contains(propertyName))
                    {
                        checks.Add(ContractCheck(
                            "shader_graph_route",
                            "error",
                            "fail",
                            $"{path} binds {propertyName}, but the graph property has no serialized route to an output block.",
                            "serialized_graph_reachability"));
                    }
                }
            }

            int failCount = checks.Count(check =>
                string.Equals(JObject.FromObject(check)["status"]?.ToString(), "fail", StringComparison.Ordinal));
            int warningCount = checks.Count(check =>
                string.Equals(JObject.FromObject(check)["severity"]?.ToString(), "warning", StringComparison.Ordinal));
            bool passed = failCount == 0 && (!strict || unknownCount == 0);
            return new SuccessResponse(
                passed ? "Render contract passed." : "Render contract failed.",
                new
                {
                    schema_version = "unity-mcp/render-contract@1",
                    passed,
                    strict,
                    material_count = materials.Count,
                    check_count = checks.Count,
                    failure_count = failCount,
                    warning_count = warningCount,
                    unknown_count = unknownCount,
                    checks,
                    contract_registry = RenderingAssetUtility.ContractRegistryVersion,
                    proof = new
                    {
                        level = "editor_asset_and_live_scene_contract",
                        fail_closed = strict,
                        excludes = new[] { "visual acceptance", "Player build", "target GPU" },
                    },
                });
        }

        private static object RenderProbe(ToolParams parameters)
        {
            int width = Math.Max(64, Math.Min(4096, parameters.GetInt("width") ?? 1024));
            int height = Math.Max(64, Math.Min(4096, parameters.GetInt("height") ?? 1024));
            int warmupFrames = Math.Max(0, Math.Min(8, parameters.GetInt("warmup_frames") ?? 1));
            string channel = parameters.Get("channel", "color").ToLowerInvariant();
            string scope = parameters.Get("scope", "scene").ToLowerInvariant();
            if (channel != "color" && channel != "wireframe")
            {
                return new ErrorResponse("Unsupported channel. Supported channels are color and wireframe.");
            }
            if (scope != "scene" && scope != "target")
            {
                return new ErrorResponse("Unsupported scope. Supported scopes are scene and target.");
            }

            Camera camera = ResolveCamera(parameters.Get("camera"));
            if (camera == null)
            {
                return new ErrorResponse("No camera could be resolved. Provide camera or add an enabled scene camera.");
            }
            GameObject target = null;
            if (scope == "target")
            {
                target = RenderingAssetUtility.ResolveGameObject(parameters.Get("target"));
                if (target == null)
                {
                    return new ErrorResponse("scope=target requires a resolvable target GameObject.");
                }
            }

            if (!RenderingAssetUtility.TryResolveOutputPath(
                parameters.Get("output_path"),
                out string projectRelativePath,
                out string fullPath,
                out string outputError))
            {
                return new ErrorResponse(outputError);
            }

            int originalQuality = QualitySettings.GetQualityLevel();
            int? requestedQuality = parameters.GetInt("quality_level");
            int appliedQuality = originalQuality;
            bool originalWireframe = GL.wireframe;
            RenderTexture originalTarget = camera.targetTexture;
            RenderTexture originalActive = RenderTexture.active;
            Vector3 originalCameraPosition = camera.transform.position;
            Quaternion originalCameraRotation = camera.transform.rotation;
            float originalFieldOfView = camera.fieldOfView;
            bool originalOrthographic = camera.orthographic;
            float originalOrthographicSize = camera.orthographicSize;
            float originalNearClip = camera.nearClipPlane;
            float originalFarClip = camera.farClipPlane;
            byte[] png;
            string captureMechanism;
            try
            {
                if (requestedQuality.HasValue)
                {
                    appliedQuality = Math.Max(0, Math.Min(QualitySettings.names.Length - 1, requestedQuality.Value));
                    QualitySettings.SetQualityLevel(appliedQuality, false);
                }
                GL.wireframe = channel == "wireframe";

                if (scope == "target")
                {
                    png = CaptureTargetPreview(camera, target, width, height, warmupFrames);
                    captureMechanism = "PreviewRenderUtility isolated target";
                }
                else
                {
                    png = CaptureSceneCamera(camera, width, height, warmupFrames);
                    captureMechanism = "Camera.Render scene";
                }
            }
            finally
            {
                GL.wireframe = originalWireframe;
                camera.targetTexture = originalTarget;
                RenderTexture.active = originalActive;
                if (QualitySettings.GetQualityLevel() != originalQuality)
                {
                    QualitySettings.SetQualityLevel(originalQuality, false);
                }
            }

            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllBytes(fullPath, png);
            if (projectRelativePath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                AssetDatabase.ImportAsset(projectRelativePath, ImportAssetOptions.ForceSynchronousImport);
            }
            string sha256 = RenderingAssetUtility.ComputeSha256(png);

            return new SuccessResponse("Render probe captured and state restored.", new
            {
                schema_version = "unity-mcp/render-probe@1",
                output_path = projectRelativePath,
                output_sha256 = sha256,
                bytes = png.Length,
                width,
                height,
                channel,
                scope,
                warmup_frames = warmupFrames,
                quality_level = appliedQuality,
                quality_name = QualitySettings.names.Length > appliedQuality
                    ? QualitySettings.names[appliedQuality]
                    : null,
                camera = new
                {
                    name = camera.name,
                    hierarchy_path = RenderingAssetUtility.GetHierarchyPath(camera.gameObject),
                    instance_id = camera.GetInstanceID(),
                    position = Vector3Record(originalCameraPosition),
                    rotation = Vector3Record(originalCameraRotation.eulerAngles),
                    field_of_view = originalFieldOfView,
                    orthographic = originalOrthographic,
                    orthographic_size = originalOrthographicSize,
                    near_clip = originalNearClip,
                    far_clip = originalFarClip,
                },
                target = target == null ? null : new
                {
                    name = target.name,
                    hierarchy_path = RenderingAssetUtility.GetHierarchyPath(target),
                    instance_id = target.GetInstanceID(),
                },
                capture_mechanism = captureMechanism,
                restoration = new
                {
                    camera_target_texture_restored = ReferenceEquals(camera.targetTexture, originalTarget),
                    render_texture_active_restored = ReferenceEquals(RenderTexture.active, originalActive),
                    quality_level_restored = QualitySettings.GetQualityLevel() == originalQuality,
                    wireframe_restored = GL.wireframe == originalWireframe,
                    camera_transform_restored = camera.transform.position == originalCameraPosition
                        && camera.transform.rotation == originalCameraRotation,
                    camera_projection_restored = Mathf.Approximately(camera.fieldOfView, originalFieldOfView)
                        && camera.orthographic == originalOrthographic
                        && Mathf.Approximately(camera.orthographicSize, originalOrthographicSize)
                        && Mathf.Approximately(camera.nearClipPlane, originalNearClip)
                        && Mathf.Approximately(camera.farClipPlane, originalFarClip),
                    scene_saved = false,
                },
                proof = new
                {
                    level = scope == "target" ? "isolated_editor_render" : "scene_editor_render",
                    locked_manifest = true,
                    limitations = new[] { "No Player/target-hardware timing proof." },
                },
            });
        }

        private static object SampleMaterial(ToolParams parameters)
        {
            string materialPath = RenderingAssetUtility.NormalizeAssetPath(parameters.Get("material_path"));
            if (!RenderingAssetUtility.IsExactAssetPath(materialPath))
            {
                return new ErrorResponse("material_path must be an exact path under Assets/ or Packages/.");
            }
            Material sourceMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (sourceMaterial == null)
            {
                return new ErrorResponse($"Could not load Material at '{materialPath}'.");
            }
            if (sourceMaterial.shader == null)
            {
                return new ErrorResponse($"Material '{materialPath}' has no shader.");
            }

            string comparisonPath = RenderingAssetUtility.NormalizeAssetPath(
                parameters.Get("compare_to_material_path"));
            Material comparisonMaterial = null;
            if (!string.IsNullOrEmpty(comparisonPath))
            {
                if (!RenderingAssetUtility.IsExactAssetPath(comparisonPath))
                {
                    return new ErrorResponse(
                        "compare_to_material_path must be an exact path under Assets/ or Packages/.");
                }
                comparisonMaterial = AssetDatabase.LoadAssetAtPath<Material>(comparisonPath);
                if (comparisonMaterial == null)
                {
                    return new ErrorResponse($"Could not load comparison Material at '{comparisonPath}'.");
                }
                if (comparisonMaterial.shader == null)
                {
                    return new ErrorResponse($"Comparison Material '{comparisonPath}' has no shader.");
                }
            }

            string requestedProfile = parameters.Get("profile", "auto").Trim().ToLowerInvariant();
            if (!new[] { "auto", "pbr", "tiled", "foliage", "transparent" }.Contains(requestedProfile))
            {
                return new ErrorResponse(
                    "Unsupported profile. Supported profiles are auto, pbr, tiled, foliage, and transparent.");
            }
            string cacheMode = parameters.Get("cache_mode", "use").Trim().ToLowerInvariant();
            if (!new[] { "use", "refresh", "bypass" }.Contains(cacheMode))
            {
                return new ErrorResponse("Unsupported cache_mode. Supported modes are use, refresh, and bypass.");
            }

            JToken overrideToken = parameters.GetRaw("property_overrides");
            if (overrideToken?.Type == JTokenType.String)
            {
                try
                {
                    overrideToken = JToken.Parse(overrideToken.ToString());
                }
                catch (JsonException exception)
                {
                    return new ErrorResponse($"property_overrides is not valid JSON: {exception.Message}");
                }
            }
            if (overrideToken != null
                && overrideToken.Type != JTokenType.Null
                && overrideToken.Type != JTokenType.Object)
            {
                return new ErrorResponse(
                    "property_overrides must be a JSON object keyed by shader property name.");
            }
            JObject propertyOverrides = overrideToken as JObject ?? new JObject();

            int maxResolution = Math.Max(256, Math.Min(512, parameters.GetInt("max_resolution") ?? 384));
            int warmupFrames = Math.Max(0, Math.Min(4, parameters.GetInt("warmup_frames") ?? 1));
            bool includeImage = parameters.GetBool("include_image", true);
            if (!TryResolveMaterialSampleOutputPath(
                parameters.Get("output_path"),
                out string outputPath,
                out string fullOutputPath,
                out string outputError))
            {
                return new ErrorResponse(outputError);
            }

            string selectedProfile = ResolveMaterialSampleProfile(
                sourceMaterial,
                materialPath,
                requestedProfile,
                out List<string> profileReasons);
            List<MaterialSampleView> views = BuildMaterialSampleViews(selectedProfile);
            int materialCount = comparisonMaterial == null ? 1 : 2;
            int panelCount = views.Count * materialCount;
            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(panelCount)));
            int rows = Math.Max(1, (int)Math.Ceiling(panelCount / (double)columns));
            int panelSize = Math.Max(32, Math.Min(maxResolution / columns, maxResolution / rows));
            int sheetWidth = panelSize * columns;
            int sheetHeight = panelSize * rows;

            bool sourceDirtyBefore = EditorUtility.IsDirty(sourceMaterial);
            bool comparisonDirtyBefore = comparisonMaterial != null && EditorUtility.IsDirty(comparisonMaterial);
            UnityEngine.SceneManagement.Scene activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            bool sceneDirtyBefore = activeScene.IsValid() && activeScene.isDirty;
            int qualityBefore = QualitySettings.GetQualityLevel();
            RenderTexture activeRenderTextureBefore = RenderTexture.active;
            Material primaryClone = new(sourceMaterial)
            {
                name = $"{sourceMaterial.name} (MCP Material Sample)",
                hideFlags = HideFlags.HideAndDontSave,
            };
            Material comparisonClone = comparisonMaterial == null
                ? null
                : new Material(comparisonMaterial)
                {
                    name = $"{comparisonMaterial.name} (MCP Material Sample Comparison)",
                    hideFlags = HideFlags.HideAndDontSave,
                };

            List<object> appliedOverrides = new();
            byte[] png = null;
            string cacheKey = null;
            string cachePath = null;
            string fullCachePath = null;
            bool cacheHit = false;
            List<object> panelManifest = BuildMaterialSamplePanelManifest(
                views,
                materialPath,
                comparisonPath,
                materialCount,
                columns,
                rows,
                panelSize,
                sheetHeight);
            List<string> contextRequirements = BuildMaterialSampleContextRequirements(sourceMaterial, materialPath);
            if (comparisonMaterial != null)
            {
                foreach (string requirement in BuildMaterialSampleContextRequirements(
                    comparisonMaterial,
                    comparisonPath))
                {
                    if (!contextRequirements.Contains(requirement))
                    {
                        contextRequirements.Add(requirement);
                    }
                }
            }

            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            string pipelinePath = pipeline == null ? null : AssetDatabase.GetAssetPath(pipeline);
            string primaryDependencyHash = AssetDatabase.GetAssetDependencyHash(materialPath).ToString();
            string comparisonDependencyHash = comparisonMaterial == null
                ? null
                : AssetDatabase.GetAssetDependencyHash(comparisonPath).ToString();
            string pipelineDependencyHash = string.IsNullOrEmpty(pipelinePath)
                ? null
                : AssetDatabase.GetAssetDependencyHash(pipelinePath).ToString();
            try
            {
                if (!TryApplyMaterialSampleOverrides(
                    primaryClone,
                    propertyOverrides,
                    appliedOverrides,
                    out string overrideError))
                {
                    return new ErrorResponse(overrideError);
                }

                string cachePayload = JsonConvert.SerializeObject(new
                {
                    sampler = "unity-mcp/material-sampler@1",
                    material_path = materialPath,
                    material_dependency_hash = primaryDependencyHash,
                    comparison_material_path = comparisonPath,
                    comparison_dependency_hash = comparisonDependencyHash,
                    pipeline_path = pipelinePath,
                    pipeline_dependency_hash = pipelineDependencyHash,
                    pipeline_type = pipeline?.GetType().FullName ?? "BuiltInRenderPipeline",
                    color_space = PlayerSettings.colorSpace.ToString(),
                    quality_level = qualityBefore,
                    quality_name = QualitySettings.names.Length > qualityBefore
                        ? QualitySettings.names[qualityBefore]
                        : null,
                    selected_profile = selectedProfile,
                    views = views.Select(MaterialSampleViewRecord).ToArray(),
                    overrides = appliedOverrides,
                    max_resolution = maxResolution,
                    sheet_width = sheetWidth,
                    sheet_height = sheetHeight,
                    panel_size = panelSize,
                    warmup_frames = warmupFrames,
                }, Formatting.None);
                cacheKey = RenderingAssetUtility.ComputeSha256(Encoding.UTF8.GetBytes(cachePayload));
                cachePath = $"Library/MCPForUnity/MaterialSamples/Cache/{cacheKey}.png";
                fullCachePath = GetProjectFullPath(cachePath);

                if (cacheMode == "use" && File.Exists(fullCachePath))
                {
                    byte[] cached = File.ReadAllBytes(fullCachePath);
                    if (IsPng(cached))
                    {
                        png = cached;
                        cacheHit = true;
                    }
                }
                if (png == null)
                {
                    png = CaptureMaterialSampleContactSheet(
                        primaryClone,
                        comparisonClone,
                        views,
                        panelSize,
                        columns,
                        rows,
                        warmupFrames);
                    if (cacheMode != "bypass")
                    {
                        WriteBytes(fullCachePath, png);
                        TrimMaterialSampleCache(Path.GetDirectoryName(fullCachePath), 128);
                    }
                }

                if (!string.IsNullOrEmpty(fullOutputPath))
                {
                    WriteBytes(fullOutputPath, png);
                }
            }
            finally
            {
                RenderTexture.active = activeRenderTextureBefore;
                UnityEngine.Object.DestroyImmediate(primaryClone);
                if (comparisonClone != null)
                {
                    UnityEngine.Object.DestroyImmediate(comparisonClone);
                }
            }

            bool sourceDirtyAfter = EditorUtility.IsDirty(sourceMaterial);
            bool comparisonDirtyAfter = comparisonMaterial != null && EditorUtility.IsDirty(comparisonMaterial);
            bool sceneDirtyAfter = activeScene.IsValid() && activeScene.isDirty;
            List<object> validationChecks = BuildMaterialSampleValidation(sourceMaterial, materialPath);
            if (comparisonMaterial != null)
            {
                validationChecks.AddRange(BuildMaterialSampleValidation(comparisonMaterial, comparisonPath));
            }
            string pngSha256 = RenderingAssetUtility.ComputeSha256(png);

            return new SuccessResponse("Material sample rendered from an isolated locked manifest.", new
            {
                schema_version = "unity-mcp/material-sample@1",
                material = BuildMaterialRecord(sourceMaterial, materialPath, false, 1, 0),
                comparison_material = comparisonMaterial == null
                    ? null
                    : BuildMaterialRecord(comparisonMaterial, comparisonPath, false, 1, 0),
                profile = new
                {
                    requested = requestedProfile,
                    selected = selectedProfile,
                    reasons = profileReasons,
                },
                property_overrides = new
                {
                    scope = "primary_temporary_clone_only",
                    count = appliedOverrides.Count,
                    applied = appliedOverrides,
                },
                preview = new
                {
                    mime_type = "image/png",
                    png_base64 = includeImage ? Convert.ToBase64String(png) : null,
                    image_included = includeImage,
                    output_path = outputPath,
                    output_sha256 = pngSha256,
                    bytes = png.Length,
                    width = sheetWidth,
                    height = sheetHeight,
                    max_resolution = maxResolution,
                    panel_size = panelSize,
                    columns,
                    rows,
                    panels = panelManifest,
                },
                locked_manifest = new
                {
                    sampler_version = "unity-mcp/material-sampler@1",
                    primary = new
                    {
                        path = materialPath,
                        guid = AssetDatabase.AssetPathToGUID(materialPath),
                        sha256 = RenderingAssetUtility.ComputeSha256(materialPath),
                        dependency_hash = primaryDependencyHash,
                        shader = sourceMaterial.shader.name,
                        shader_path = AssetDatabase.GetAssetPath(sourceMaterial.shader),
                    },
                    comparison = comparisonMaterial == null ? null : new
                    {
                        path = comparisonPath,
                        guid = AssetDatabase.AssetPathToGUID(comparisonPath),
                        sha256 = RenderingAssetUtility.ComputeSha256(comparisonPath),
                        dependency_hash = comparisonDependencyHash,
                        shader = comparisonMaterial.shader.name,
                        shader_path = AssetDatabase.GetAssetPath(comparisonMaterial.shader),
                    },
                    render_pipeline = new
                    {
                        name = pipeline?.name ?? "Built-in Render Pipeline",
                        type = pipeline?.GetType().FullName ?? "BuiltInRenderPipeline",
                        path = pipelinePath,
                        dependency_hash = pipelineDependencyHash,
                    },
                    color_space = PlayerSettings.colorSpace.ToString(),
                    quality_level = qualityBefore,
                    quality_name = QualitySettings.names.Length > qualityBefore
                        ? QualitySettings.names[qualityBefore]
                        : null,
                    warmup_frames = warmupFrames,
                    views = views.Select(MaterialSampleViewRecord).ToArray(),
                    comparison_layout = comparisonMaterial == null
                        ? "single_material"
                        : "same_views_side_by_side",
                },
                context = new
                {
                    requires_scene_probe = contextRequirements.Count > 0,
                    requirements = contextRequirements,
                    next_step = contextRequirements.Count > 0
                        ? "Use render_probe on the actual scene owner with a locked camera and manifest."
                        : null,
                },
                validation = new
                {
                    check_count = validationChecks.Count,
                    warning_count = validationChecks.Count(check =>
                        string.Equals(JObject.FromObject(check)["status"]?.ToString(), "warning", StringComparison.Ordinal)),
                    checks = validationChecks,
                },
                cache = new
                {
                    mode = cacheMode,
                    key = cacheKey,
                    hit = cacheHit,
                    path = cacheMode == "bypass" ? null : cachePath,
                    maximum_entries = 128,
                },
                restoration = new
                {
                    source_material_dirty_before = sourceDirtyBefore,
                    source_material_dirty_after = sourceDirtyAfter,
                    source_material_dirty_unchanged = sourceDirtyBefore == sourceDirtyAfter,
                    comparison_material_dirty_before = comparisonMaterial == null ? (bool?)null : comparisonDirtyBefore,
                    comparison_material_dirty_after = comparisonMaterial == null ? (bool?)null : comparisonDirtyAfter,
                    comparison_material_dirty_unchanged = comparisonMaterial == null
                        ? (bool?)null
                        : comparisonDirtyBefore == comparisonDirtyAfter,
                    scene_dirty_before = sceneDirtyBefore,
                    scene_dirty_after = sceneDirtyAfter,
                    scene_dirty_unchanged = sceneDirtyBefore == sceneDirtyAfter,
                    render_texture_active_restored = ReferenceEquals(RenderTexture.active, activeRenderTextureBefore),
                    quality_level_restored = QualitySettings.GetQualityLevel() == qualityBefore,
                    project_assets_written = false,
                },
                proof = new
                {
                    level = "isolated_editor_material_sample",
                    locked_manifest = true,
                    source_assets_untouched = sourceDirtyBefore == sourceDirtyAfter
                        && (comparisonMaterial == null || comparisonDirtyBefore == comparisonDirtyAfter),
                    excludes = new[]
                    {
                        "actual scene lighting, probes, lightmaps, decals, and renderer-feature state",
                        "Player build behavior",
                        "target GPU behavior or timing",
                        "visual acceptance without a locked scene render probe",
                    },
                },
            });
        }

        private static object ProfileRenderTarget(ToolParams parameters)
        {
            string targetValue = parameters.Get("target");
            GameObject gameObject = RenderingAssetUtility.ResolveGameObject(targetValue);
            if (gameObject == null)
            {
                return new ErrorResponse($"Could not resolve scene GameObject '{targetValue}'.");
            }

            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            HashSet<int> rendererIds = renderers.Select(renderer => renderer.gameObject.GetInstanceID()).ToHashSet();
            long triangles = 0;
            int submeshes = 0;
            int materialSlots = 0;
            int shaderPasses = 0;
            foreach (Renderer renderer in renderers)
            {
                Mesh mesh = GetRendererMesh(renderer);
                if (mesh != null)
                {
                    submeshes += mesh.subMeshCount;
                    for (int index = 0; index < mesh.subMeshCount; index++)
                    {
                        triangles += (long)mesh.GetIndexCount(index) / 3L;
                    }
                }
                materialSlots += renderer.sharedMaterials.Length;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null)
                    {
                        shaderPasses += material.passCount;
                    }
                }
            }

            object frameDebugger = new
            {
                requested = false,
                available = false,
                events = Array.Empty<object>(),
                proof = "not_requested",
            };
            if (parameters.GetBool("include_frame_debugger", true))
            {
                JObject frameParams = new()
                {
                    ["page_size"] = ClampPageSize(parameters.GetInt("page_size") ?? 50),
                    ["cursor"] = Math.Max(0, parameters.GetInt("cursor") ?? 0),
                };
                object response = FrameDebuggerOps.GetEvents(frameParams);
                if (response is SuccessResponse success && success.Data != null)
                {
                    JObject raw = JObject.FromObject(success.Data);
                    JArray allEvents = raw["events"] as JArray ?? new JArray();
                    List<JToken> matching = allEvents
                        .Where(item => rendererIds.Contains(item["gameObjectInstanceID"]?.Value<int>() ?? int.MinValue))
                        .ToList();
                    frameDebugger = new
                    {
                        requested = true,
                        available = (raw["total_events"]?.Value<int>() ?? 0) > 0,
                        total_captured_events = raw["total_events"]?.Value<int>() ?? 0,
                        matching_events = matching,
                        page_size = raw["page_size"]?.Value<int>(),
                        cursor = raw["cursor"]?.Value<int>(),
                        next_cursor = raw["next_cursor"]?.Value<int?>(),
                        proof = matching.Count > 0 ? "captured_frame_debugger_events" : "no_matching_event_in_requested_page",
                    };
                }
                else
                {
                    frameDebugger = new
                    {
                        requested = true,
                        available = false,
                        events = Array.Empty<object>(),
                        proof = "frame_debugger_unavailable_or_disabled",
                        response,
                    };
                }
            }

            return new SuccessResponse($"Profiled render target '{gameObject.name}'.", new
            {
                schema_version = "unity-mcp/render-target-profile@1",
                target = new
                {
                    name = gameObject.name,
                    hierarchy_path = RenderingAssetUtility.GetHierarchyPath(gameObject),
                    instance_id = gameObject.GetInstanceID(),
                },
                static_evidence = new
                {
                    renderer_count = renderers.Length,
                    material_slot_count = materialSlots,
                    shader_pass_upper_bound = shaderPasses,
                    submesh_count = submeshes,
                    triangle_count = triangles,
                    renderers = renderers.Select(BuildRendererRecord).ToList(),
                },
                frame_debugger = frameDebugger,
                proof = new
                {
                    static_level = "live_editor_scene_and_imported_assets",
                    dynamic_level = parameters.GetBool("include_frame_debugger", true)
                        ? "current_frame_debugger_snapshot_if_available"
                        : "not_requested",
                    limitations = new[]
                    {
                        "Static pass count is an upper bound; keywords and pipeline choose actual passes.",
                        "No Player/target GPU timings are inferred from Editor data.",
                    },
                },
            });
        }

        private static object BuildMaterialRecord(
            Material material,
            string materialPath,
            bool includeConsumers,
            int pageSize,
            int cursor)
        {
            Shader shader = material.shader;
            string shaderPath = shader == null ? null : AssetDatabase.GetAssetPath(shader);
            string shaderGuid = string.IsNullOrEmpty(shaderPath) ? null : AssetDatabase.AssetPathToGUID(shaderPath);
            string shaderKind = GetShaderKind(shaderPath);
            List<object> properties = new();
            Material defaults = shader == null ? null : new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                int propertyCount = shader == null ? 0 : shader.GetPropertyCount();
                for (int index = 0; index < propertyCount; index++)
                {
                    string propertyName = shader.GetPropertyName(index);
                    ShaderPropertyType propertyType = shader.GetPropertyType(index);
                    object current = GetMaterialPropertyValue(material, propertyName, propertyType);
                    object defaultValue = defaults == null ? null : GetMaterialPropertyValue(defaults, propertyName, propertyType);
                    object textureBinding = null;
                    if (propertyType == ShaderPropertyType.Texture)
                    {
                        Texture texture = material.GetTexture(propertyName);
                        string texturePath = texture == null ? null : AssetDatabase.GetAssetPath(texture);
                        textureBinding = new
                        {
                            name = texture?.name,
                            path = texturePath,
                            guid = string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.AssetPathToGUID(texturePath),
                            scale = Vector2Record(material.GetTextureScale(propertyName)),
                            offset = Vector2Record(material.GetTextureOffset(propertyName)),
                            ownership = RenderingAssetUtility.ClassifyOwnership(texturePath),
                            semantic_contract = texture == null
                                ? null
                                : RenderingAssetUtility.ClassifyTextureContract(texturePath, null),
                        };
                    }
                    properties.Add(new
                    {
                        name = propertyName,
                        display_name = shader.GetPropertyDescription(index),
                        type = propertyType.ToString(),
                        current_value = current,
                        default_value = defaultValue,
                        differs_from_default = !JToken.DeepEquals(
                            current == null ? JValue.CreateNull() : JToken.FromObject(current),
                            defaultValue == null ? JValue.CreateNull() : JToken.FromObject(defaultValue)),
                        texture = textureBinding,
                        attributes = shader.GetPropertyAttributes(index),
                    });
                }
            }
            finally
            {
                if (defaults != null)
                {
                    UnityEngine.Object.DestroyImmediate(defaults);
                }
            }

            List<object> consumers = new();
            int totalConsumers = 0;
            if (includeConsumers)
            {
                List<(Renderer renderer, int slot)> matches = new();
                Renderer[] allRenderers = UnityEngine.Resources.FindObjectsOfTypeAll<Renderer>();
                foreach (Renderer renderer in allRenderers)
                {
                    if (renderer == null || EditorUtility.IsPersistent(renderer))
                    {
                        continue;
                    }
                    Material[] materials = renderer.sharedMaterials;
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        if (ReferenceEquals(materials[slot], material))
                        {
                            matches.Add((renderer, slot));
                        }
                    }
                }
                matches = matches
                    .OrderBy(match => RenderingAssetUtility.GetHierarchyPath(match.renderer.gameObject), StringComparer.Ordinal)
                    .ThenBy(match => match.slot)
                    .ToList();
                totalConsumers = matches.Count;
                int end = Math.Min(cursor + pageSize, matches.Count);
                for (int index = cursor; index < end; index++)
                {
                    (Renderer renderer, int slot) match = matches[index];
                    consumers.Add(new
                    {
                        renderer = match.renderer.name,
                        renderer_type = match.renderer.GetType().FullName,
                        hierarchy_path = RenderingAssetUtility.GetHierarchyPath(match.renderer.gameObject),
                        game_object_instance_id = match.renderer.gameObject.GetInstanceID(),
                        renderer_instance_id = match.renderer.GetInstanceID(),
                        slot = match.slot,
                        scene_path = match.renderer.gameObject.scene.path,
                        lod = GetLodMembership(match.renderer),
                    });
                }
            }

            Dictionary<string, object> consumerData = new()
            {
                ["items"] = consumers,
                ["total"] = totalConsumers,
                ["cursor"] = cursor,
                ["page_size"] = pageSize,
            };
            if (cursor + consumers.Count < totalConsumers)
            {
                consumerData["next_cursor"] = cursor + consumers.Count;
            }

            return new
            {
                schema_version = "unity-mcp/material-inspection@1",
                asset = new
                {
                    name = material.name,
                    path = materialPath,
                    guid = AssetDatabase.AssetPathToGUID(materialPath),
                    sha256 = RenderingAssetUtility.ComputeSha256(materialPath),
                    ownership = RenderingAssetUtility.ClassifyOwnership(materialPath),
                },
                shader = new
                {
                    name = shader?.name,
                    path = shaderPath,
                    guid = shaderGuid,
                    kind = shaderKind,
                    supported = shader?.isSupported,
                    pass_count = material.passCount,
                    passes = GetShaderPasses(shader),
                    compiler_messages = GetShaderMessages(shader),
                    srp_batcher = GetSrpBatcherEvidence(shader),
                },
                properties,
                keywords = material.shaderKeywords.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                state = new
                {
                    render_queue = material.renderQueue,
                    render_type = material.GetTag("RenderType", false, string.Empty),
                    disable_batching = material.GetTag("DisableBatching", false, string.Empty),
                    instancing = material.enableInstancing,
                    double_sided_gi = material.doubleSidedGI,
                    global_illumination_flags = material.globalIlluminationFlags.ToString(),
                    cull = TryGetFloat(material, "_Cull"),
                    surface = TryGetFloat(material, "_Surface"),
                    alpha_clip = TryGetFloat(material, "_AlphaClip"),
                    z_write = TryGetFloat(material, "_ZWrite"),
                    source_blend = TryGetFloat(material, "_SrcBlend"),
                    destination_blend = TryGetFloat(material, "_DstBlend"),
                },
                consumers = consumerData,
                proof = new
                {
                    level = "exact_material_and_live_scene_consumers",
                    consumer_scope = includeConsumers ? "loaded_editor_objects" : "not_requested",
                    no_project_wide_asset_hydration = true,
                },
            };
        }

        private static Dictionary<string, object> BuildRendererRecord(Renderer renderer)
        {
            MaterialPropertyBlock block = new();
            renderer.GetPropertyBlock(block);
            List<object> slots = new();
            Material[] materials = renderer.sharedMaterials;
            for (int index = 0; index < materials.Length; index++)
            {
                Material material = materials[index];
                string materialPath = material == null ? null : AssetDatabase.GetAssetPath(material);
                MaterialPropertyBlock slotBlock = new();
                renderer.GetPropertyBlock(slotBlock, index);
                slots.Add(new
                {
                    slot = index,
                    material_name = material?.name,
                    material_path = materialPath,
                    material_guid = string.IsNullOrEmpty(materialPath) ? null : AssetDatabase.AssetPathToGUID(materialPath),
                    shader_name = material?.shader?.name,
                    shader_path = material?.shader == null ? null : AssetDatabase.GetAssetPath(material.shader),
                    has_property_block = !slotBlock.isEmpty,
                    property_block_overrides = BuildPropertyBlockOverrides(slotBlock, material),
                    renderer_property_block_overrides = BuildPropertyBlockOverrides(block, material),
                    ownership = RenderingAssetUtility.ClassifyOwnership(materialPath),
                });
            }

            Mesh mesh = GetRendererMesh(renderer);
            List<object> submeshes = new();
            if (mesh != null)
            {
                for (int index = 0; index < mesh.subMeshCount; index++)
                {
                    submeshes.Add(new
                    {
                        index,
                        topology = mesh.GetTopology(index).ToString(),
                        index_count = mesh.GetIndexCount(index),
                        base_vertex = mesh.GetBaseVertex(index),
                        bounds = BoundsRecord(mesh.GetSubMesh(index).bounds),
                    });
                }
            }

            return new Dictionary<string, object>
            {
                ["name"] = renderer.name,
                ["renderer_type"] = renderer.GetType().FullName,
                ["hierarchy_path"] = RenderingAssetUtility.GetHierarchyPath(renderer.gameObject),
                ["game_object_instance_id"] = renderer.gameObject.GetInstanceID(),
                ["renderer_instance_id"] = renderer.GetInstanceID(),
                ["enabled"] = renderer.enabled,
                ["active_in_hierarchy"] = renderer.gameObject.activeInHierarchy,
                ["force_rendering_off"] = renderer.forceRenderingOff,
                ["shadow_casting_mode"] = renderer.shadowCastingMode.ToString(),
                ["receive_shadows"] = renderer.receiveShadows,
                ["light_probe_usage"] = renderer.lightProbeUsage.ToString(),
                ["reflection_probe_usage"] = renderer.reflectionProbeUsage.ToString(),
                ["lightmap_index"] = renderer.lightmapIndex,
                ["realtime_lightmap_index"] = renderer.realtimeLightmapIndex,
                ["sorting_layer"] = SortingLayer.IDToName(renderer.sortingLayerID),
                ["sorting_order"] = renderer.sortingOrder,
                ["renderer_property_block"] = !block.isEmpty,
                ["renderer_property_block_override_count"] = materials
                    .SelectMany(material => BuildPropertyBlockOverrides(block, material))
                    .Count(),
                ["materials"] = slots,
                ["mesh"] = mesh == null ? null : new
                {
                    name = mesh.name,
                    asset_path = AssetDatabase.GetAssetPath(mesh),
                    guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(mesh)),
                    vertex_count = mesh.vertexCount,
                    submesh_count = mesh.subMeshCount,
                    bounds = BoundsRecord(mesh.bounds),
                    submeshes,
                },
                ["lod"] = GetLodMembership(renderer),
                ["prefab_asset_path"] = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(renderer.gameObject),
            };
        }

        private static object GetLodMembership(Renderer renderer)
        {
            LODGroup group = renderer.GetComponentInParent<LODGroup>();
            if (group == null)
            {
                return new { member = false };
            }
            LOD[] lods = group.GetLODs();
            for (int index = 0; index < lods.Length; index++)
            {
                if (lods[index].renderers.Contains(renderer))
                {
                    return new
                    {
                        member = true,
                        group_name = group.name,
                        group_instance_id = group.GetInstanceID(),
                        group_path = RenderingAssetUtility.GetHierarchyPath(group.gameObject),
                        lod_index = index,
                        screen_relative_transition_height = lods[index].screenRelativeTransitionHeight,
                        fade_transition_width = lods[index].fadeTransitionWidth,
                        lod_count = lods.Length,
                    };
                }
            }
            return new
            {
                member = false,
                nearest_group = group.name,
                group_path = RenderingAssetUtility.GetHierarchyPath(group.gameObject),
            };
        }

        private static List<object> BuildPropertyBlockOverrides(
            MaterialPropertyBlock block,
            Material material)
        {
            List<object> values = new();
            Shader shader = material?.shader;
            if (block == null || block.isEmpty || shader == null)
            {
                return values;
            }

            int propertyCount = shader.GetPropertyCount();
            for (int index = 0; index < propertyCount; index++)
            {
                string propertyName = shader.GetPropertyName(index);
                if (!block.HasProperty(propertyName))
                {
                    continue;
                }
                ShaderPropertyType propertyType = shader.GetPropertyType(index);
                object value;
                switch (propertyType)
                {
                    case ShaderPropertyType.Color:
                    {
                        Color color = block.GetColor(propertyName);
                        value = new { r = color.r, g = color.g, b = color.b, a = color.a };
                        break;
                    }
                    case ShaderPropertyType.Vector:
                    {
                        Vector4 vector = block.GetVector(propertyName);
                        value = new { x = vector.x, y = vector.y, z = vector.z, w = vector.w };
                        break;
                    }
                    case ShaderPropertyType.Int:
                    {
                        value = block.GetInteger(propertyName);
                        break;
                    }
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        value = block.GetFloat(propertyName);
                        break;
                    }
                    case ShaderPropertyType.Texture:
                    {
                        Texture texture = block.GetTexture(propertyName);
                        string texturePath = texture == null ? null : AssetDatabase.GetAssetPath(texture);
                        value = new
                        {
                            name = texture?.name,
                            path = texturePath,
                            guid = string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.AssetPathToGUID(texturePath),
                        };
                        break;
                    }
                    default:
                    {
                        continue;
                    }
                }
                values.Add(new
                {
                    name = propertyName,
                    type = propertyType.ToString(),
                    value,
                });
            }
            return values;
        }

        private static Mesh GetRendererMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                return skinned.sharedMesh;
            }
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter?.sharedMesh;
        }

        private static object GetMaterialPropertyValue(
            Material material,
            string propertyName,
            ShaderPropertyType propertyType)
        {
            if (material == null || !material.HasProperty(propertyName))
            {
                return null;
            }
            switch (propertyType)
            {
                case ShaderPropertyType.Color:
                {
                    Color value = material.GetColor(propertyName);
                    return new { r = value.r, g = value.g, b = value.b, a = value.a };
                }
                case ShaderPropertyType.Vector:
                {
                    Vector4 value = material.GetVector(propertyName);
                    return new { x = value.x, y = value.y, z = value.z, w = value.w };
                }
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                {
                    return material.GetFloat(propertyName);
                }
                case ShaderPropertyType.Int:
                {
                    return material.GetInteger(propertyName);
                }
                case ShaderPropertyType.Texture:
                {
                    Texture texture = material.GetTexture(propertyName);
                    return texture == null ? null : new
                    {
                        name = texture.name,
                        path = AssetDatabase.GetAssetPath(texture),
                        guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(texture)),
                    };
                }
                default:
                {
                    return null;
                }
            }
        }

        private static float? TryGetFloat(Material material, string propertyName)
        {
            return material != null && material.HasProperty(propertyName)
                ? material.GetFloat(propertyName)
                : null;
        }

        private static string GetShaderKind(string shaderPath)
        {
            string extension = Path.GetExtension(shaderPath ?? string.Empty).ToLowerInvariant();
            switch (extension)
            {
                case ".shadergraph":
                {
                    return "ShaderGraph";
                }
                case ".shadersubgraph":
                {
                    return "ShaderSubGraph";
                }
                case ".shader":
                {
                    return "ShaderLab";
                }
                default:
                {
                    return string.IsNullOrEmpty(shaderPath) ? "BuiltInOrGenerated" : "Unknown";
                }
            }
        }

        private static object BuildShaderAssetIdentity(string shaderPath, Shader shader, string kind)
        {
            return new
            {
                path = shaderPath,
                guid = AssetDatabase.AssetPathToGUID(shaderPath),
                sha256 = RenderingAssetUtility.ComputeSha256(shaderPath),
                name = shader?.name,
                kind,
                ownership = RenderingAssetUtility.ClassifyOwnership(shaderPath),
            };
        }

        private static List<object> GetShaderPasses(Shader shader)
        {
            List<object> passes = new();
            if (shader == null)
            {
                return passes;
            }
            Material material = new(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                for (int index = 0; index < material.passCount; index++)
                {
                    passes.Add(new { index, name = material.GetPassName(index) });
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
            return passes;
        }

        private static List<object> GetShaderMessages(Shader shader)
        {
            List<object> messages = new();
            if (shader == null)
            {
                return messages;
            }
            Array rawMessages = ShaderUtil.GetShaderMessages(shader);
            foreach (object raw in rawMessages)
            {
                Type type = raw.GetType();
                messages.Add(new
                {
                    message = GetMemberValue(type, raw, "message")?.ToString(),
                    severity = GetMemberValue(type, raw, "severity")?.ToString(),
                    platform = GetMemberValue(type, raw, "platform")?.ToString(),
                    file = GetMemberValue(type, raw, "file")?.ToString(),
                    line = GetMemberValue(type, raw, "line"),
                });
            }
            return messages;
        }

        private static object GetSrpBatcherEvidence(Shader shader)
        {
            if (shader == null)
            {
                return new { status = "unknown", evidence = "shader_missing" };
            }
            try
            {
                MethodInfo method = typeof(ShaderUtil).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => candidate.Name.Contains("SRPBatcherCompatibility", StringComparison.Ordinal));
                if (method == null)
                {
                    return new
                    {
                        status = "unknown",
                        evidence = "Unity exposes no compatible SRP Batcher inspection method in this Editor version.",
                    };
                }
                ParameterInfo[] parameterInfos = method.GetParameters();
                object[] arguments = parameterInfos.Select(info =>
                    info.ParameterType == typeof(Shader) ? (object)shader
                    : info.ParameterType == typeof(int) ? 0
                    : info.HasDefaultValue ? info.DefaultValue
                    : null).ToArray();
                object result = method.Invoke(null, arguments);
                return new
                {
                    status = result == null ? "unknown" : "reported",
                    compatibility_code = result?.ToString(),
                    evidence = $"ShaderUtil.{method.Name}",
                };
            }
            catch (Exception exception)
            {
                return new
                {
                    status = "unknown",
                    evidence = $"Reflection failed: {exception.GetBaseException().Message}",
                };
            }
        }

        private static object GetMemberValue(Type type, object instance, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                return field.GetValue(instance);
            }
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property?.GetValue(instance);
        }

        private static object SampleTexture(
            Texture texture,
            int sampleSize,
            TextureSemanticContract contract)
        {
            int width = Math.Max(1, Math.Min(sampleSize, texture.width));
            int height = Math.Max(1, Math.Min(sampleSize, texture.height));
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            Texture2D readback = null;
            try
            {
                UnityEngine.Graphics.Blit(texture, temporary);
                RenderTexture.active = temporary;
                readback = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readback.Apply(false, false);
                Color[] pixels = readback.GetPixels();
                double[] minimum = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
                double[] maximum = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
                double[] sum = new double[4];
                double[] sumSquares = new double[4];
                int alphaCovered = 0;
                int invalidNormals = 0;
                foreach (Color pixel in pixels)
                {
                    double[] values = { pixel.r, pixel.g, pixel.b, pixel.a };
                    for (int channel = 0; channel < 4; channel++)
                    {
                        minimum[channel] = Math.Min(minimum[channel], values[channel]);
                        maximum[channel] = Math.Max(maximum[channel], values[channel]);
                        sum[channel] += values[channel];
                        sumSquares[channel] += values[channel] * values[channel];
                    }
                    if (pixel.a >= 0.5f)
                    {
                        alphaCovered++;
                    }

                    if (contract.Name == "freshcan_n_ao_r")
                    {
                        float normalX = pixel.r * 2f - 1f;
                        float normalY = pixel.g * 2f - 1f;
                        if (normalX * normalX + normalY * normalY > 1.0005f)
                        {
                            invalidNormals++;
                        }
                    }
                    else if (contract.Name == "normal")
                    {
                        Vector3 normal = new(pixel.r * 2f - 1f, pixel.g * 2f - 1f, pixel.b * 2f - 1f);
                        float length = normal.magnitude;
                        if (length < 0.75f || length > 1.25f)
                        {
                            invalidNormals++;
                        }
                    }
                }

                List<object> channels = new();
                string[] names = { "r", "g", "b", "a" };
                for (int channel = 0; channel < 4; channel++)
                {
                    double mean = sum[channel] / pixels.Length;
                    double variance = Math.Max(0d, sumSquares[channel] / pixels.Length - mean * mean);
                    channels.Add(new
                    {
                        channel = names[channel],
                        minimum = minimum[channel],
                        maximum = maximum[channel],
                        mean,
                        standard_deviation = Math.Sqrt(variance),
                    });
                }

                double horizontalSeam = 0d;
                double verticalSeam = 0d;
                for (int y = 0; y < height; y++)
                {
                    horizontalSeam += ColorDistance(pixels[y * width], pixels[y * width + width - 1]);
                }
                for (int x = 0; x < width; x++)
                {
                    verticalSeam += ColorDistance(pixels[x], pixels[(height - 1) * width + x]);
                }

                return new
                {
                    sample_width = width,
                    sample_height = height,
                    sample_count = pixels.Length,
                    sample_path = "GPU blit to bounded linear RGBA32 readback",
                    channels,
                    alpha_coverage_at_0_5 = (double)alphaCovered / pixels.Length,
                    opposite_edge_mean_absolute_difference = new
                    {
                        horizontal = horizontalSeam / height,
                        vertical = verticalSeam / width,
                    },
                    normal_validity = contract.Name == "freshcan_n_ao_r" || contract.Name == "normal"
                        ? new
                        {
                            checked_pixels = pixels.Length,
                            invalid_pixels = invalidNormals,
                            invalid_fraction = (double)invalidNormals / pixels.Length,
                            criterion = contract.Name == "freshcan_n_ao_r"
                                ? "decoded XY length must not exceed one"
                                : "decoded RGB vector length must be within 0.75-1.25",
                        }
                        : null,
                    channel_thumbnails = BuildChannelThumbnails(pixels, width, height),
                };
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (readback != null)
                {
                    UnityEngine.Object.DestroyImmediate(readback);
                }
            }
        }

        private static Dictionary<string, string> BuildChannelThumbnails(Color[] pixels, int width, int height)
        {
            const int thumbnailSize = 32;
            string[] names = { "r", "g", "b", "a" };
            Dictionary<string, string> thumbnails = new();
            for (int channel = 0; channel < 4; channel++)
            {
                Texture2D thumbnail = new(thumbnailSize, thumbnailSize, TextureFormat.RGBA32, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                Color32[] values = new Color32[thumbnailSize * thumbnailSize];
                for (int y = 0; y < thumbnailSize; y++)
                {
                    int sourceY = Math.Min(height - 1, y * height / thumbnailSize);
                    for (int x = 0; x < thumbnailSize; x++)
                    {
                        int sourceX = Math.Min(width - 1, x * width / thumbnailSize);
                        Color source = pixels[sourceY * width + sourceX];
                        float scalar = channel switch
                        {
                            0 => source.r,
                            1 => source.g,
                            2 => source.b,
                            _ => source.a,
                        };
                        byte value = (byte)Mathf.RoundToInt(Mathf.Clamp01(scalar) * 255f);
                        values[y * thumbnailSize + x] = new Color32(value, value, value, 255);
                    }
                }
                thumbnail.SetPixels32(values);
                thumbnail.Apply(false, false);
                thumbnails[names[channel]] = Convert.ToBase64String(thumbnail.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(thumbnail);
            }
            return thumbnails;
        }

        private static double ColorDistance(Color first, Color second)
        {
            return (Math.Abs(first.r - second.r)
                + Math.Abs(first.g - second.g)
                + Math.Abs(first.b - second.b)
                + Math.Abs(first.a - second.a)) / 4d;
        }

        private static object Vector2Record(Vector2 value)
        {
            return new { x = value.x, y = value.y };
        }

        private static object Vector3Record(Vector3 value)
        {
            return new { x = value.x, y = value.y, z = value.z };
        }

        private static object BoundsRecord(Bounds value)
        {
            return new
            {
                center = Vector3Record(value.center),
                size = Vector3Record(value.size),
                extents = Vector3Record(value.extents),
                minimum = Vector3Record(value.min),
                maximum = Vector3Record(value.max),
            };
        }

        private static GraphAnalysis AnalyzeShaderGraph(string shaderPath)
        {
            string fullPath = RenderingAssetUtility.GetFullPath(shaderPath);
            ShaderGraphDocumentFile file = ShaderGraphDocumentFile.Load(fullPath);
            ShaderGraphDocument root = file.FindGraphRoot();
            if (root == null)
            {
                throw new InvalidDataException("Could not identify the Shader Graph root document.");
            }

            GraphAnalysis result = new()
            {
                File = file,
                Root = root,
                GraphVersion = root.Value["m_SGVersion"]
                    ?? root.Value["m_Version"]
                    ?? root.Value["m_SerializationVersion"],
            };
            Dictionary<string, ShaderGraphDocument> byId = file.Documents
                .Where(document => !string.IsNullOrEmpty(document.ObjectId))
                .GroupBy(document => document.ObjectId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            List<string> nodeIds = ExtractReferenceIds(root.Value["m_Nodes"]);
            List<string> propertyIds = ExtractReferenceIds(root.Value["m_Properties"]);
            List<string> targetIds = ExtractReferenceIds(root.Value["m_ActiveTargets"] ?? root.Value["m_Targets"]);
            Dictionary<string, ShaderGraphDocument> nodes = nodeIds
                .Where(byId.ContainsKey)
                .ToDictionary(id => id, id => byId[id], StringComparer.Ordinal);
            Dictionary<string, string> slotOwners = new(StringComparer.Ordinal);
            HashSet<string> emittedSlots = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, ShaderGraphDocument> pair in nodes)
            {
                foreach (string slotId in ExtractReferenceIds(pair.Value.Value["m_Slots"]))
                {
                    slotOwners[slotId] = pair.Key;
                    if (emittedSlots.Add(slotId) && byId.TryGetValue(slotId, out ShaderGraphDocument slot))
                    {
                        result.Slots.Add(new
                        {
                            object_id = slotId,
                            node_id = pair.Key,
                            type = slot.TypeName,
                            display_name = slot.Value["m_DisplayName"]?.ToString()
                                ?? slot.Value["m_Name"]?.ToString(),
                            slot_id = slot.Value["m_Id"]?.Value<int?>()
                                ?? slot.Value["m_SlotId"]?.Value<int?>(),
                            direction = slot.Value["m_SlotType"]?.ToString()
                                ?? slot.Value["m_Direction"]?.ToString(),
                            value = slot.Value["m_Value"]?.DeepClone()
                                ?? slot.Value["m_DefaultValue"]?.DeepClone(),
                        });
                    }
                }
                result.Nodes.Add(new
                {
                    object_id = pair.Key,
                    type = pair.Value.TypeName,
                    name = pair.Value.Value["m_Name"]?.ToString(),
                    slot_ids = ExtractReferenceIds(pair.Value.Value["m_Slots"]),
                });
                if (pair.Value.TypeName?.Contains("SubGraph", StringComparison.OrdinalIgnoreCase) == true)
                {
                    result.Subgraphs.Add(new
                    {
                        node_id = pair.Key,
                        type = pair.Value.TypeName,
                        asset_guid = FindFirstGuid(pair.Value.Value),
                    });
                }
            }

            foreach (string targetId in targetIds)
            {
                if (byId.TryGetValue(targetId, out ShaderGraphDocument targetDocument))
                {
                    result.Targets.Add(new
                    {
                        object_id = targetId,
                        type = targetDocument.TypeName,
                        name = targetDocument.Value["m_Name"]?.ToString(),
                    });
                }
            }

            JArray rawEdges = root.Value["m_Edges"] as JArray ?? new JArray();
            Dictionary<string, HashSet<string>> adjacency = new(StringComparer.Ordinal);
            foreach (JToken edge in rawEdges)
            {
                string outputNode = edge["m_OutputSlot"]?["m_Node"]?["m_Id"]?.ToString();
                string inputNode = edge["m_InputSlot"]?["m_Node"]?["m_Id"]?.ToString();
                string outputSlot = edge["m_OutputSlot"]?["m_SlotId"]?.ToString();
                string inputSlot = edge["m_InputSlot"]?["m_SlotId"]?.ToString();
                if (string.IsNullOrEmpty(outputNode) && !string.IsNullOrEmpty(outputSlot))
                {
                    slotOwners.TryGetValue(outputSlot, out outputNode);
                }
                if (string.IsNullOrEmpty(inputNode) && !string.IsNullOrEmpty(inputSlot))
                {
                    slotOwners.TryGetValue(inputSlot, out inputNode);
                }
                result.Edges.Add(new
                {
                    output_node = outputNode,
                    output_slot = outputSlot,
                    input_node = inputNode,
                    input_slot = inputSlot,
                });
                if (!string.IsNullOrEmpty(outputNode) && !string.IsNullOrEmpty(inputNode))
                {
                    if (!adjacency.TryGetValue(outputNode, out HashSet<string> destinations))
                    {
                        destinations = new HashSet<string>(StringComparer.Ordinal);
                        adjacency[outputNode] = destinations;
                    }
                    destinations.Add(inputNode);
                }
            }

            HashSet<string> terminals = nodes
                .Where(pair => IsTerminalNode(pair.Value.TypeName))
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string propertyId in propertyIds)
            {
                if (!byId.TryGetValue(propertyId, out ShaderGraphDocument property))
                {
                    continue;
                }
                string referenceName = property.Value["m_ReferenceName"]?.ToString()
                    ?? property.Value["m_Name"]?.ToString()
                    ?? propertyId;
                string displayName = property.Value["m_DisplayName"]?.ToString()
                    ?? property.Value["m_Name"]?.ToString();
                List<string> startNodes = nodes
                    .Where(pair => ReferencesProperty(pair.Value.Value, propertyId))
                    .Select(pair => pair.Key)
                    .ToList();
                List<string> trace = FindTraceToTerminal(startNodes, adjacency, terminals);
                bool reachesOutput = trace.Count > 0;
                if (!reachesOutput)
                {
                    result.InertReferenceNames.Add(referenceName);
                }
                result.Properties.Add(new
                {
                    object_id = propertyId,
                    type = property.TypeName,
                    reference_name = referenceName,
                    display_name = displayName,
                    exposed = property.Value["m_GeneratePropertyBlock"]?.Value<bool?>(),
                    hidden = property.Value["m_Hidden"]?.Value<bool?>(),
                    serialized_version = property.Value["m_SGVersion"]
                        ?? property.Value["m_Version"]
                        ?? property.Value["m_SerializationVersion"],
                    default_value = property.Value["m_Value"]?.DeepClone()
                        ?? property.Value["m_DefaultValue"]?.DeepClone(),
                });
                result.Traces.Add(new
                {
                    property_id = propertyId,
                    reference_name = referenceName,
                    property_node_ids = startNodes,
                    reaches_output = reachesOutput,
                    node_trace = trace,
                    status = reachesOutput ? "active" : "inert",
                });
            }
            return result;
        }

        private static List<string> ExtractReferenceIds(JToken token)
        {
            if (token is not JArray array)
            {
                return new List<string>();
            }
            return array
                .Select(item => item?["m_Id"]?.ToString() ?? item?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static bool ReferencesProperty(JToken token, string propertyId)
        {
            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties())
                {
                    if ((property.Name == "m_Property" || property.Name == "m_PropertyId")
                        && (property.Value?["m_Id"]?.ToString() == propertyId
                            || property.Value?.ToString() == propertyId
                            || (property.Value?.Type == JTokenType.String
                                && property.Value.ToString().Contains(propertyId, StringComparison.Ordinal))))
                    {
                        return true;
                    }
                    if (ReferencesProperty(property.Value, propertyId))
                    {
                        return true;
                    }
                }
            }
            else if (token is JArray array)
            {
                return array.Any(item => ReferencesProperty(item, propertyId));
            }
            return false;
        }

        private static bool IsTerminalNode(string typeName)
        {
            return typeName?.Contains("BlockNode", StringComparison.OrdinalIgnoreCase) == true
                || typeName?.Contains("MasterNode", StringComparison.OrdinalIgnoreCase) == true
                || typeName?.Contains("OutputNode", StringComparison.OrdinalIgnoreCase) == true;
        }

        private static List<string> FindTraceToTerminal(
            IEnumerable<string> starts,
            Dictionary<string, HashSet<string>> adjacency,
            HashSet<string> terminals)
        {
            Queue<string> queue = new();
            Dictionary<string, string> previous = new(StringComparer.Ordinal);
            foreach (string start in starts.Distinct(StringComparer.Ordinal))
            {
                queue.Enqueue(start);
                previous[start] = null;
            }
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (terminals.Contains(current))
                {
                    List<string> path = new();
                    string item = current;
                    while (item != null)
                    {
                        path.Add(item);
                        item = previous[item];
                    }
                    path.Reverse();
                    return path;
                }
                if (!adjacency.TryGetValue(current, out HashSet<string> destinations))
                {
                    continue;
                }
                foreach (string destination in destinations)
                {
                    if (previous.ContainsKey(destination))
                    {
                        continue;
                    }
                    previous[destination] = current;
                    queue.Enqueue(destination);
                }
            }
            return new List<string>();
        }

        private static string FindFirstGuid(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties())
                {
                    if (property.Name.Contains("guid", StringComparison.OrdinalIgnoreCase)
                        && property.Value.Type == JTokenType.String
                        && property.Value.ToString().Length >= 32)
                    {
                        return property.Value.ToString();
                    }
                    string nested = FindFirstGuid(property.Value);
                    if (!string.IsNullOrEmpty(nested))
                    {
                        return nested;
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    string nested = FindFirstGuid(item);
                    if (!string.IsNullOrEmpty(nested))
                    {
                        return nested;
                    }
                }
            }
            return null;
        }

        private static object ContractCheck(
            string check,
            string severity,
            string status,
            string message,
            string proof)
        {
            return new { check, severity, status, message, proof };
        }

        private static void AddTextureContractCheck(
            string owner,
            string propertyName,
            Texture texture,
            string requestedContract,
            bool strict,
            List<object> checks,
            ref int unknownCount,
            string bindingProof)
        {
            string texturePath = AssetDatabase.GetAssetPath(texture);
            TextureSemanticContract contract = RenderingAssetUtility.ClassifyTextureContract(
                texturePath,
                requestedContract);
            TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (!contract.IsKnown)
            {
                unknownCount++;
                checks.Add(ContractCheck(
                    "texture_semantic_contract",
                    strict ? "error" : "warning",
                    strict ? "fail" : "unknown",
                    $"{owner} {propertyName} -> {texturePath}: semantic contract is unknown.",
                    bindingProof));
                return;
            }
            if (importer == null)
            {
                checks.Add(ContractCheck(
                    "texture_importer",
                    "error",
                    "fail",
                    $"{texturePath}: TextureImporter is unavailable.",
                    bindingProof));
                return;
            }

            bool importerMatches = (!contract.ExpectedSrgb.HasValue || importer.sRGBTexture == contract.ExpectedSrgb.Value)
                && (string.IsNullOrEmpty(contract.ExpectedImporterType)
                    || string.Equals(importer.textureType.ToString(), contract.ExpectedImporterType, StringComparison.OrdinalIgnoreCase))
                && (!contract.ExpectedMipmaps.HasValue || importer.mipmapEnabled == contract.ExpectedMipmaps.Value);
            checks.Add(ContractCheck(
                "texture_importer_contract",
                importerMatches ? "info" : "error",
                importerMatches ? "pass" : "fail",
                $"{owner} {propertyName} -> {texturePath}: {contract.Name}; sRGB={importer.sRGBTexture}, type={importer.textureType}, mips={importer.mipmapEnabled}.",
                bindingProof));
        }

        private static string ResolveMaterialSampleProfile(
            Material material,
            string materialPath,
            string requestedProfile,
            out List<string> reasons)
        {
            reasons = new List<string>();
            if (requestedProfile != "auto")
            {
                reasons.Add("Caller selected the profile explicitly.");
                return requestedProfile;
            }

            string shaderPath = material.shader == null ? null : AssetDatabase.GetAssetPath(material.shader);
            string identity = string.Join(" ", new[]
            {
                material.name,
                materialPath,
                material.shader?.name,
                shaderPath,
                material.GetTag("RenderType", false, string.Empty),
                string.Join(" ", material.shaderKeywords),
            }).ToLowerInvariant();
            bool transparent = material.renderQueue >= 3000
                || identity.Contains("transparent")
                || (TryGetFloat(material, "_Surface") ?? 0f) > 0.5f;
            if (transparent)
            {
                reasons.Add("Render queue, RenderType, shader identity, or _Surface indicates transparency.");
                return "transparent";
            }

            bool cutout = identity.Contains("vegetation")
                || identity.Contains("foliage")
                || identity.Contains("leaf")
                || identity.Contains("grass")
                || identity.Contains("transparentcutout")
                || (TryGetFloat(material, "_AlphaClip") ?? 0f) > 0.5f;
            if (cutout)
            {
                reasons.Add("Shader/material identity or alpha clipping indicates foliage or cutout rendering.");
                return "foliage";
            }

            if (identity.Contains("triplanar")
                || identity.Contains("microsplat")
                || identity.Contains("terrain")
                || identity.Contains("tiled"))
            {
                reasons.Add("Shader/material identity indicates tiled, triplanar, or terrain mapping.");
                return "tiled";
            }

            reasons.Add("No transparent, cutout/foliage, or tiled/triplanar signal was found.");
            return "pbr";
        }

        private static List<MaterialSampleView> BuildMaterialSampleViews(string profile)
        {
            Color dark = new(0.035f, 0.04f, 0.05f, 1f);
            Color mid = new(0.12f, 0.13f, 0.15f, 1f);
            Color light = new(0.78f, 0.80f, 0.84f, 1f);
            Color ambient = new(0.18f, 0.19f, 0.21f, 1f);
            switch (profile)
            {
                case "tiled":
                {
                    return new List<MaterialSampleView>
                    {
                        NewMaterialSampleView("cube", "cube", new Vector3(0f, 0f, -3.8f),
                            Vector3.zero, Vector3.one, new Vector3(18f, -28f, 0f), mid,
                            new Vector3(48f, -32f, 0f), 1.25f, 0.15f, ambient),
                        NewMaterialSampleView("grazing_plane", "quad", new Vector3(0f, 0f, -3.2f),
                            Vector3.zero, new Vector3(1.45f, 1.45f, 1f), new Vector3(63f, 0f, 0f), mid,
                            new Vector3(55f, -24f, 0f), 1.35f, 0.1f, ambient),
                    };
                }
                case "foliage":
                {
                    return new List<MaterialSampleView>
                    {
                        NewMaterialSampleView("front_card", "quad", new Vector3(0f, 0f, -3.0f),
                            Vector3.zero, new Vector3(1.35f, 1.35f, 1f), Vector3.zero, mid,
                            new Vector3(40f, -25f, 0f), 1.2f, 0.1f, ambient),
                        NewMaterialSampleView("back_card", "quad", new Vector3(0f, 0f, -3.0f),
                            Vector3.zero, new Vector3(1.35f, 1.35f, 1f), new Vector3(0f, 180f, 0f), mid,
                            new Vector3(40f, 155f, 0f), 1.2f, 0.1f, ambient),
                        NewMaterialSampleView("backlit_card", "quad", new Vector3(0f, 0f, -3.0f),
                            Vector3.zero, new Vector3(1.35f, 1.35f, 1f), new Vector3(0f, 18f, 0f), dark,
                            new Vector3(25f, -20f, 0f), 0.25f, 1.8f, new Color(0.05f, 0.05f, 0.06f, 1f)),
                    };
                }
                case "transparent":
                {
                    return new List<MaterialSampleView>
                    {
                        NewMaterialSampleView("dark_background", "quad", new Vector3(0f, 0f, -3.0f),
                            Vector3.zero, new Vector3(1.35f, 1.35f, 1f), Vector3.zero, dark,
                            new Vector3(42f, -28f, 0f), 1.2f, 0.1f, ambient),
                        NewMaterialSampleView("light_background", "quad", new Vector3(0f, 0f, -3.0f),
                            Vector3.zero, new Vector3(1.35f, 1.35f, 1f), Vector3.zero, light,
                            new Vector3(42f, -28f, 0f), 1.2f, 0.1f, new Color(0.32f, 0.32f, 0.32f, 1f)),
                        NewMaterialSampleView("oblique_card", "quad", new Vector3(0f, 0f, -3.0f),
                            Vector3.zero, new Vector3(1.35f, 1.35f, 1f), new Vector3(0f, 52f, 0f), mid,
                            new Vector3(35f, -35f, 0f), 1.3f, 0.2f, ambient),
                    };
                }
                default:
                {
                    return new List<MaterialSampleView>
                    {
                        NewMaterialSampleView("sphere", "sphere", new Vector3(0f, 0f, -3.4f),
                            Vector3.zero, Vector3.one, new Vector3(0f, 22f, 0f), mid,
                            new Vector3(48f, -32f, 0f), 1.25f, 0.15f, ambient),
                        NewMaterialSampleView("grazing_plane", "quad", new Vector3(0f, 0f, -3.2f),
                            Vector3.zero, new Vector3(1.45f, 1.45f, 1f), new Vector3(63f, 0f, 0f), mid,
                            new Vector3(55f, -24f, 0f), 1.35f, 0.1f, ambient),
                    };
                }
            }
        }

        private static MaterialSampleView NewMaterialSampleView(
            string name,
            string meshKind,
            Vector3 cameraPosition,
            Vector3 cameraEuler,
            Vector3 objectScale,
            Vector3 objectEuler,
            Color background,
            Vector3 keyLightEuler,
            float keyLightIntensity,
            float backLightIntensity,
            Color ambient)
        {
            return new MaterialSampleView
            {
                Name = name,
                MeshKind = meshKind,
                CameraPosition = cameraPosition,
                CameraEuler = cameraEuler,
                ObjectScale = objectScale,
                ObjectEuler = objectEuler,
                Background = background,
                KeyLightEuler = keyLightEuler,
                KeyLightIntensity = keyLightIntensity,
                BackLightIntensity = backLightIntensity,
                Ambient = ambient,
            };
        }

        private static object MaterialSampleViewRecord(MaterialSampleView view)
        {
            return new
            {
                name = view.Name,
                mesh = view.MeshKind,
                camera = new
                {
                    position = Vector3Record(view.CameraPosition),
                    rotation = Vector3Record(view.CameraEuler),
                    field_of_view = 30f,
                    near_clip = 0.01f,
                    far_clip = 20f,
                },
                geometry = new
                {
                    scale = Vector3Record(view.ObjectScale),
                    rotation = Vector3Record(view.ObjectEuler),
                },
                lighting = new
                {
                    key_rotation = Vector3Record(view.KeyLightEuler),
                    key_intensity = view.KeyLightIntensity,
                    back_intensity = view.BackLightIntensity,
                    ambient = ColorRecord(view.Ambient),
                },
                background = ColorRecord(view.Background),
            };
        }

        private static object ColorRecord(Color value)
        {
            return new { r = value.r, g = value.g, b = value.b, a = value.a };
        }

        private static List<object> BuildMaterialSamplePanelManifest(
            List<MaterialSampleView> views,
            string materialPath,
            string comparisonPath,
            int materialCount,
            int columns,
            int rows,
            int panelSize,
            int sheetHeight)
        {
            List<object> panels = new();
            int cell = 0;
            foreach (MaterialSampleView view in views)
            {
                for (int materialIndex = 0; materialIndex < materialCount; materialIndex++)
                {
                    int column = cell % columns;
                    int row = cell / columns;
                    int x = column * panelSize;
                    int yBottom = sheetHeight - ((row + 1) * panelSize);
                    panels.Add(new
                    {
                        index = cell,
                        view = view.Name,
                        role = materialIndex == 0 ? "primary" : "comparison",
                        material_path = materialIndex == 0 ? materialPath : comparisonPath,
                        x,
                        y_top = row * panelSize,
                        y_bottom = yBottom,
                        width = panelSize,
                        height = panelSize,
                    });
                    cell++;
                }
            }
            return panels;
        }

        private static byte[] CaptureMaterialSampleContactSheet(
            Material primary,
            Material comparison,
            List<MaterialSampleView> views,
            int panelSize,
            int columns,
            int rows,
            int warmupFrames)
        {
            int width = columns * panelSize;
            int height = rows * panelSize;
            Texture2D sheet = new(width, height, TextureFormat.RGBA32, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            try
            {
                Color32[] background = Enumerable.Repeat(
                    new Color32(12, 13, 16, 255),
                    width * height).ToArray();
                sheet.SetPixels32(background);
                Material[] materials = comparison == null
                    ? new[] { primary }
                    : new[] { primary, comparison };
                int cell = 0;
                foreach (MaterialSampleView view in views)
                {
                    foreach (Material material in materials)
                    {
                        Color32[] pixels = CaptureMaterialSamplePanel(
                            material,
                            view,
                            panelSize,
                            warmupFrames);
                        int column = cell % columns;
                        int row = cell / columns;
                        int x = column * panelSize;
                        int y = height - ((row + 1) * panelSize);
                        sheet.SetPixels32(x, y, panelSize, panelSize, pixels);
                        cell++;
                    }
                }
                sheet.Apply(false, false);
                return sheet.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        private static Color32[] CaptureMaterialSamplePanel(
            Material material,
            MaterialSampleView view,
            int panelSize,
            int warmupFrames)
        {
            PreviewRenderUtility preview = new();
            Texture2D image = null;
            try
            {
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = view.Background;
                preview.camera.fieldOfView = 30f;
                preview.camera.nearClipPlane = 0.01f;
                preview.camera.farClipPlane = 20f;
                preview.camera.transform.SetPositionAndRotation(
                    view.CameraPosition,
                    Quaternion.Euler(view.CameraEuler));
                preview.ambientColor = view.Ambient;
                if (preview.lights.Length > 0)
                {
                    preview.lights[0].type = LightType.Directional;
                    preview.lights[0].color = Color.white;
                    preview.lights[0].intensity = view.KeyLightIntensity;
                    preview.lights[0].transform.rotation = Quaternion.Euler(view.KeyLightEuler);
                }
                if (preview.lights.Length > 1)
                {
                    preview.lights[1].type = LightType.Directional;
                    preview.lights[1].color = new Color(0.65f, 0.75f, 1f, 1f);
                    preview.lights[1].intensity = view.BackLightIntensity;
                    preview.lights[1].transform.rotation = Quaternion.Euler(
                        -view.KeyLightEuler.x,
                        view.KeyLightEuler.y + 180f,
                        0f);
                }

                Mesh mesh = ResolveMaterialSampleMesh(view.MeshKind);
                preview.BeginStaticPreview(new Rect(0f, 0f, panelSize, panelSize));
                Matrix4x4 matrix = Matrix4x4.TRS(
                    Vector3.zero,
                    Quaternion.Euler(view.ObjectEuler),
                    view.ObjectScale);
                for (int subMesh = 0; subMesh < Math.Max(1, mesh.subMeshCount); subMesh++)
                {
                    preview.DrawMesh(mesh, matrix, material, subMesh);
                }
                for (int index = 0; index < warmupFrames; index++)
                {
                    preview.Render(true);
                }
                preview.Render(true);
                image = preview.EndStaticPreview();
                if (image == null)
                {
                    throw new InvalidOperationException($"Preview rendering returned no image for view '{view.Name}'.");
                }
                return image.GetPixels32();
            }
            finally
            {
                if (image != null)
                {
                    UnityEngine.Object.DestroyImmediate(image);
                }
                preview.Cleanup();
            }
        }

        private static Mesh ResolveMaterialSampleMesh(string meshKind)
        {
            string[] names = meshKind == "sphere"
                ? new[] { "New-Sphere.fbx", "Sphere.fbx" }
                : meshKind == "cube"
                    ? new[] { "Cube.fbx", "New-Cube.fbx" }
                    : new[] { "Quad.fbx", "New-Quad.fbx" };
            foreach (string name in names)
            {
                Mesh mesh = UnityEngine.Resources.GetBuiltinResource<Mesh>(name);
                if (mesh != null)
                {
                    return mesh;
                }
            }
            throw new InvalidOperationException(
                $"Unity built-in preview mesh '{meshKind}' could not be resolved.");
        }

        private static bool TryApplyMaterialSampleOverrides(
            Material material,
            JObject overrides,
            List<object> applied,
            out string error)
        {
            error = null;
            Shader shader = material.shader;
            foreach (JProperty property in overrides.Properties().OrderBy(
                item => item.Name,
                StringComparer.Ordinal))
            {
                int propertyIndex = -1;
                for (int index = 0; index < shader.GetPropertyCount(); index++)
                {
                    if (string.Equals(shader.GetPropertyName(index), property.Name, StringComparison.Ordinal))
                    {
                        propertyIndex = index;
                        break;
                    }
                }
                if (propertyIndex < 0 || !material.HasProperty(property.Name))
                {
                    error = $"Shader '{shader.name}' has no material property '{property.Name}'.";
                    return false;
                }

                ShaderPropertyType propertyType = shader.GetPropertyType(propertyIndex);
                switch (propertyType)
                {
                    case ShaderPropertyType.Color:
                    {
                        if (!TryReadVector4(property.Value, true, out Vector4 vector, out error))
                        {
                            error = $"Override '{property.Name}' must be a color array/object: {error}";
                            return false;
                        }
                        Color color = new(vector.x, vector.y, vector.z, vector.w);
                        material.SetColor(property.Name, color);
                        applied.Add(new
                        {
                            property = property.Name,
                            type = "Color",
                            value = ColorRecord(color),
                        });
                        break;
                    }
                    case ShaderPropertyType.Vector:
                    {
                        if (!TryReadVector4(property.Value, false, out Vector4 vector, out error))
                        {
                            error = $"Override '{property.Name}' must be a vector array/object: {error}";
                            return false;
                        }
                        material.SetVector(property.Name, vector);
                        applied.Add(new
                        {
                            property = property.Name,
                            type = "Vector",
                            value = new { x = vector.x, y = vector.y, z = vector.z, w = vector.w },
                        });
                        break;
                    }
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        if (!TryReadFloat(property.Value, out float value))
                        {
                            error = $"Override '{property.Name}' must be numeric.";
                            return false;
                        }
                        material.SetFloat(property.Name, value);
                        applied.Add(new
                        {
                            property = property.Name,
                            type = propertyType.ToString(),
                            value,
                        });
                        break;
                    }
                    case ShaderPropertyType.Int:
                    {
                        if (!TryReadFloat(property.Value, out float numeric)
                            || Math.Abs(numeric - Math.Round(numeric)) > 0.0001f)
                        {
                            error = $"Override '{property.Name}' must be an integer.";
                            return false;
                        }
                        int value = (int)Math.Round(numeric);
                        material.SetInteger(property.Name, value);
                        applied.Add(new
                        {
                            property = property.Name,
                            type = "Int",
                            value,
                        });
                        break;
                    }
                    case ShaderPropertyType.Texture:
                    {
                        if (!TryApplyMaterialSampleTextureOverride(
                            material,
                            property.Name,
                            property.Value,
                            out object record,
                            out error))
                        {
                            return false;
                        }
                        applied.Add(record);
                        break;
                    }
                    default:
                    {
                        error = $"Override '{property.Name}' uses unsupported type '{propertyType}'.";
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool TryApplyMaterialSampleTextureOverride(
            Material material,
            string propertyName,
            JToken token,
            out object record,
            out string error)
        {
            record = null;
            error = null;
            string texturePath = null;
            JToken scaleToken = null;
            JToken offsetToken = null;
            if (token == null || token.Type == JTokenType.Null)
            {
                texturePath = null;
            }
            else if (token.Type == JTokenType.String)
            {
                texturePath = RenderingAssetUtility.NormalizeAssetPath(token.ToString());
            }
            else if (token is JObject textureObject)
            {
                texturePath = RenderingAssetUtility.NormalizeAssetPath(
                    textureObject["texture_path"]?.ToString() ?? textureObject["path"]?.ToString());
                scaleToken = textureObject["scale"];
                offsetToken = textureObject["offset"];
            }
            else
            {
                error = $"Override '{propertyName}' must be a texture path, null, or texture object.";
                return false;
            }

            Texture texture = null;
            if (!string.IsNullOrEmpty(texturePath))
            {
                if (!RenderingAssetUtility.IsExactAssetPath(texturePath))
                {
                    error = $"Override '{propertyName}' texture path must be exact under Assets/ or Packages/.";
                    return false;
                }
                texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                if (texture == null)
                {
                    error = $"Could not load override Texture at '{texturePath}'.";
                    return false;
                }
            }
            material.SetTexture(propertyName, texture);

            Vector2 scale = material.GetTextureScale(propertyName);
            if (scaleToken != null)
            {
                if (!TryReadVector2(scaleToken, out scale, out error))
                {
                    error = $"Override '{propertyName}' texture scale is invalid: {error}";
                    return false;
                }
                material.SetTextureScale(propertyName, scale);
            }
            Vector2 offset = material.GetTextureOffset(propertyName);
            if (offsetToken != null)
            {
                if (!TryReadVector2(offsetToken, out offset, out error))
                {
                    error = $"Override '{propertyName}' texture offset is invalid: {error}";
                    return false;
                }
                material.SetTextureOffset(propertyName, offset);
            }
            record = new
            {
                property = propertyName,
                type = "Texture",
                value = new
                {
                    path = texturePath,
                    guid = string.IsNullOrEmpty(texturePath)
                        ? null
                        : AssetDatabase.AssetPathToGUID(texturePath),
                    dependency_hash = string.IsNullOrEmpty(texturePath)
                        ? null
                        : AssetDatabase.GetAssetDependencyHash(texturePath).ToString(),
                    scale = Vector2Record(scale),
                    offset = Vector2Record(offset),
                },
            };
            return true;
        }

        private static bool TryReadVector4(
            JToken token,
            bool color,
            out Vector4 value,
            out string error)
        {
            value = color ? new Vector4(0f, 0f, 0f, 1f) : Vector4.zero;
            error = null;
            if (token is JArray array)
            {
                int minimum = color ? 3 : 2;
                if (array.Count < minimum || array.Count > 4)
                {
                    error = $"expected {minimum}-4 numeric components";
                    return false;
                }
                float[] components = new float[4] { 0f, 0f, 0f, color ? 1f : 0f };
                for (int index = 0; index < array.Count; index++)
                {
                    if (!TryReadFloat(array[index], out components[index]))
                    {
                        error = $"component {index} is not numeric";
                        return false;
                    }
                }
                value = new Vector4(components[0], components[1], components[2], components[3]);
                return true;
            }
            if (token is JObject valueObject)
            {
                string[] names = color
                    ? new[] { "r", "g", "b", "a" }
                    : new[] { "x", "y", "z", "w" };
                int minimum = color ? 3 : 2;
                float[] components = new float[4] { 0f, 0f, 0f, color ? 1f : 0f };
                for (int index = 0; index < names.Length; index++)
                {
                    JToken component = valueObject[names[index]];
                    if (component == null)
                    {
                        if (index < minimum)
                        {
                            error = $"missing component '{names[index]}'";
                            return false;
                        }
                        continue;
                    }
                    if (!TryReadFloat(component, out components[index]))
                    {
                        error = $"component '{names[index]}' is not numeric";
                        return false;
                    }
                }
                value = new Vector4(components[0], components[1], components[2], components[3]);
                return true;
            }
            error = "expected an array or object";
            return false;
        }

        private static bool TryReadVector2(JToken token, out Vector2 value, out string error)
        {
            value = Vector2.zero;
            error = null;
            if (token is JArray array && array.Count == 2
                && TryReadFloat(array[0], out float x)
                && TryReadFloat(array[1], out float y))
            {
                value = new Vector2(x, y);
                return true;
            }
            if (token is JObject valueObject
                && TryReadFloat(valueObject["x"], out x)
                && TryReadFloat(valueObject["y"], out y))
            {
                value = new Vector2(x, y);
                return true;
            }
            error = "expected two numeric components";
            return false;
        }

        private static bool TryReadFloat(JToken token, out float value)
        {
            value = 0f;
            if (token == null
                || (token.Type != JTokenType.Integer
                    && token.Type != JTokenType.Float
                    && token.Type != JTokenType.String))
            {
                return false;
            }
            return float.TryParse(
                token.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        private static List<string> BuildMaterialSampleContextRequirements(
            Material material,
            string materialPath)
        {
            HashSet<string> requirements = new(StringComparer.Ordinal);
            Shader shader = material.shader;
            string shaderPath = shader == null ? null : AssetDatabase.GetAssetPath(shader);
            StringBuilder identityEvidence = new();
            identityEvidence.Append(material.name).Append(' ')
                .Append(materialPath).Append(' ')
                .Append(shader?.name).Append(' ')
                .Append(shaderPath).Append(' ')
                .Append(string.Join(" ", material.shaderKeywords));
            if (shader != null)
            {
                for (int index = 0; index < shader.GetPropertyCount(); index++)
                {
                    identityEvidence.Append(' ').Append(shader.GetPropertyName(index));
                }
            }
            string identityText = identityEvidence.ToString().ToLowerInvariant();
            string sourceText = string.Empty;
            string shaderFullPath = string.IsNullOrEmpty(shaderPath)
                ? null
                : RenderingAssetUtility.GetFullPath(shaderPath);
            if (!string.IsNullOrEmpty(shaderFullPath) && File.Exists(shaderFullPath))
            {
                FileInfo info = new(shaderFullPath);
                if (info.Length <= 2 * 1024 * 1024)
                {
                    sourceText = File.ReadAllText(shaderFullPath).ToLowerInvariant();
                }
            }
            string text = identityText + " " + sourceText;
            if (text.Contains("the vegetation engine")
                || text.Contains("the visual engine")
                || ContainsMaterialSampleIdentifierToken(identityText, "tve"))
            {
                requirements.Add("TVE vegetation integration and its scene/global shader state are not present in isolation.");
            }
            if (text.Contains("microsplat") || text.Contains("terrainlit") || text.Contains("terrain shader"))
            {
                requirements.Add("Terrain or MicroSplat control textures, geometry, and integration state require the actual scene owner.");
            }
            if (text.Contains("bakery")
                || text.Contains("adaptive probe volume")
                || text.Contains("probevolume")
                || ContainsMaterialSampleIdentifierToken(identityText, "apv")
                || sourceText.Contains("apv_")
                || sourceText.Contains("_apv"))
            {
                requirements.Add("Bakery, APV, or probe-volume lighting requires scene lighting/probe evidence.");
            }
            if (text.Contains("scenecolornode") || text.Contains("_cameraopaquetexture"))
            {
                requirements.Add("Scene Color or camera opaque texture sampling requires a real camera and renderer configuration.");
            }
            if (text.Contains("scenedepthnode") || text.Contains("_cameradepthtexture"))
            {
                requirements.Add("Scene Depth or camera depth texture sampling requires a real camera and renderer configuration.");
            }
            if (identityText.Contains("decal")
                || identityText.Contains("rendererfeature")
                || identityText.Contains("renderer feature")
                || sourceText.Contains("decalsubtarget"))
            {
                requirements.Add("Decal or renderer-feature participation requires the actual renderer and scene context.");
            }
            return requirements.OrderBy(value => value, StringComparer.Ordinal).ToList();
        }

        private static bool ContainsMaterialSampleIdentifierToken(string value, string token)
        {
            return (value ?? string.Empty)
                .Split(new[] { ' ', '/', '\\', '_', '-', '.', ':', '(', ')', '[', ']' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(part => string.Equals(part, token, StringComparison.Ordinal));
        }

        private static List<object> BuildMaterialSampleValidation(Material material, string materialPath)
        {
            List<object> checks = new()
            {
                new
                {
                    check = "shader_supported",
                    owner = materialPath,
                    status = material.shader != null && material.shader.isSupported ? "pass" : "warning",
                    evidence = material.shader == null
                        ? "Material has no shader."
                        : $"Shader '{material.shader.name}' isSupported={material.shader.isSupported}.",
                },
            };
            Shader shader = material.shader;
            if (shader == null)
            {
                return checks;
            }
            for (int index = 0; index < shader.GetPropertyCount(); index++)
            {
                if (shader.GetPropertyType(index) != ShaderPropertyType.Texture)
                {
                    continue;
                }
                string propertyName = shader.GetPropertyName(index);
                Texture texture = material.GetTexture(propertyName);
                if (texture == null)
                {
                    continue;
                }
                string texturePath = AssetDatabase.GetAssetPath(texture);
                TextureSemanticContract contract = RenderingAssetUtility.ClassifyTextureContract(texturePath, null);
                checks.Add(new
                {
                    check = "texture_semantic_contract",
                    owner = materialPath,
                    property = propertyName,
                    texture_path = texturePath,
                    status = contract.IsKnown ? "pass" : "warning",
                    evidence = contract.IsKnown
                        ? $"Classified as '{contract.Name}' from '{contract.Source}'."
                        : "Texture semantic contract is unknown; channel meaning is not proven.",
                });
            }
            return checks;
        }

        private static bool TryResolveMaterialSampleOutputPath(
            string requested,
            out string projectRelativePath,
            out string fullPath,
            out string error)
        {
            projectRelativePath = null;
            fullPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(requested))
            {
                return true;
            }
            string normalized = requested.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(normalized)
                || normalized.Contains(":")
                || normalized.Split('/').Any(segment => segment == "..")
                || !normalized.StartsWith(
                    "Library/MCPForUnity/MaterialSamples/",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(normalized), ".png", StringComparison.OrdinalIgnoreCase))
            {
                error = "output_path must be a relative .png path under Library/MCPForUnity/MaterialSamples/.";
                return false;
            }
            string root = GetProjectFullPath("Library/MCPForUnity/MaterialSamples");
            string candidate = GetProjectFullPath(normalized);
            string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "output_path resolves outside Library/MCPForUnity/MaterialSamples/.";
                return false;
            }
            projectRelativePath = normalized;
            fullPath = candidate;
            return true;
        }

        private static string GetProjectFullPath(string projectRelativePath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(
                projectRoot,
                projectRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void WriteBytes(string fullPath, byte[] content)
        {
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllBytes(fullPath, content);
        }

        private static bool IsPng(byte[] content)
        {
            return content != null
                && content.Length >= 8
                && content[0] == 0x89
                && content[1] == 0x50
                && content[2] == 0x4E
                && content[3] == 0x47
                && content[4] == 0x0D
                && content[5] == 0x0A
                && content[6] == 0x1A
                && content[7] == 0x0A;
        }

        private static void TrimMaterialSampleCache(string directory, int maximumEntries)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }
            try
            {
                foreach (FileInfo file in new DirectoryInfo(directory)
                    .GetFiles("*.png")
                    .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                    .Skip(maximumEntries))
                {
                    file.Delete();
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static Camera ResolveCamera(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                GameObject gameObject = RenderingAssetUtility.ResolveGameObject(value);
                Camera resolved = gameObject?.GetComponent<Camera>();
                if (resolved != null)
                {
                    return resolved;
                }
                if (int.TryParse(value, out int instanceId))
                {
                    return UnityEngine.Resources.FindObjectsOfTypeAll<Camera>()
                        .FirstOrDefault(candidate => candidate.GetInstanceID() == instanceId);
                }
            }
            if (Camera.main != null)
            {
                return Camera.main;
            }
            return UnityEngine.Resources.FindObjectsOfTypeAll<Camera>()
                .FirstOrDefault(candidate => candidate != null
                    && !EditorUtility.IsPersistent(candidate)
                    && candidate.gameObject.scene.IsValid());
        }

        private static byte[] CaptureSceneCamera(Camera camera, int width, int height, int warmupFrames)
        {
            RenderTexture renderTexture = RenderTexture.GetTemporary(
                width,
                height,
                24,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            Texture2D image = null;
            try
            {
                camera.targetTexture = renderTexture;
                for (int index = 0; index < warmupFrames; index++)
                {
                    camera.Render();
                }
                camera.Render();
                RenderTexture.active = renderTexture;
                image = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                image.Apply(false, false);
                return image.EncodeToPNG();
            }
            finally
            {
                RenderTexture.ReleaseTemporary(renderTexture);
                if (image != null)
                {
                    UnityEngine.Object.DestroyImmediate(image);
                }
            }
        }

        private static byte[] CaptureTargetPreview(
            Camera sourceCamera,
            GameObject target,
            int width,
            int height,
            int warmupFrames)
        {
            PreviewRenderUtility preview = new();
            Texture2D image = null;
            try
            {
                GameObject clone = UnityEngine.Object.Instantiate(target);
                clone.name = $"{target.name} (Render Probe Clone)";
                clone.hideFlags = HideFlags.HideAndDontSave;
                foreach (MonoBehaviour behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    behaviour.enabled = false;
                }
                preview.AddSingleGO(clone);
                preview.camera.CopyFrom(sourceCamera);
                preview.camera.transform.SetPositionAndRotation(
                    sourceCamera.transform.position,
                    sourceCamera.transform.rotation);
                if (preview.lights.Length > 0)
                {
                    Light sourceLight = RenderSettings.sun;
                    preview.lights[0].intensity = sourceLight != null ? sourceLight.intensity : 1f;
                    preview.lights[0].color = sourceLight != null ? sourceLight.color : Color.white;
                    preview.lights[0].transform.rotation = sourceLight != null
                        ? sourceLight.transform.rotation
                        : Quaternion.Euler(50f, -30f, 0f);
                }
                preview.ambientColor = RenderSettings.ambientLight;
                preview.BeginStaticPreview(new Rect(0f, 0f, width, height));
                for (int index = 0; index < warmupFrames; index++)
                {
                    preview.camera.Render();
                }
                preview.camera.Render();
                image = preview.EndStaticPreview();
                return image.EncodeToPNG();
            }
            finally
            {
                if (image != null)
                {
                    UnityEngine.Object.DestroyImmediate(image);
                }
                preview.Cleanup();
            }
        }

        private static int ClampPageSize(int value)
        {
            return Math.Max(1, Math.Min(100, value));
        }
    }
}
