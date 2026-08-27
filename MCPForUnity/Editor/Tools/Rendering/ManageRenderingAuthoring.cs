using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MCPForUnity.Editor.Tools.Rendering
{
    [McpForUnityTool(
        "manage_rendering_authoring",
        AutoRegister = false,
        Group = "rendering_authoring",
        Description = "Dry-run-first transactional material, texture-importer, and structured Shader Graph patches with SHA preconditions and ownership guards.")]
    public static class ManageRenderingAuthoring
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            ToolParams parameters = new(@params);
            string assetPath = RenderingAssetUtility.NormalizeAssetPath(parameters.Get("asset_path"));
            string assetKind = parameters.Get("asset_kind")?.Trim().ToLowerInvariant();
            bool dryRun = parameters.GetBool("dry_run", true);
            string expectedSha256 = parameters.Get("expected_sha256")?.Trim().ToLowerInvariant();
            string copyTo = RenderingAssetUtility.NormalizeAssetPath(parameters.Get("copy_to"));
            bool allowVendorAsset = parameters.GetBool("allow_vendor_asset", false);
            JArray operations = parameters.GetRaw("operations") as JArray;

            if (!RenderingAssetUtility.IsExactAssetPath(assetPath))
            {
                return new ErrorResponse("asset_path must be an exact path under Assets/ or Packages/.");
            }
            if (operations == null)
            {
                return new ErrorResponse("operations must be a JSON array.");
            }
            if (assetKind != "material" && assetKind != "texture_importer" && assetKind != "shader_graph")
            {
                return new ErrorResponse("asset_kind must be material, texture_importer, or shader_graph.");
            }
            if (!dryRun && string.IsNullOrWhiteSpace(expectedSha256))
            {
                return new ErrorResponse("expected_sha256 is required when dry_run is false.");
            }

            string fullPath = RenderingAssetUtility.GetFullPath(assetPath);
            if (fullPath == null || !File.Exists(fullPath))
            {
                return new ErrorResponse($"Asset does not exist at '{assetPath}'.");
            }
            string currentSha256 = RenderingAssetUtility.ComputeAuthoringSha256(assetPath, assetKind);
            string preconditionPath = RenderingAssetUtility.GetAuthoringPreconditionPath(assetPath, assetKind);
            if (string.IsNullOrEmpty(currentSha256))
            {
                return new ErrorResponse($"Could not hash authoring precondition '{preconditionPath}'.");
            }
            if (!dryRun && !string.Equals(currentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ErrorResponse("SHA-256 precondition failed; regenerate the dry-run plan.", new
                {
                    asset_path = assetPath,
                    precondition_path = preconditionPath,
                    expected_sha256 = expectedSha256,
                    actual_sha256 = currentSha256,
                });
            }

            RenderingOwnershipInfo ownership = RenderingAssetUtility.ClassifyOwnership(assetPath);
            bool protectedAsset = ownership.RequiresProjectCopy || ownership.IsGenerated;
            if (protectedAsset && string.IsNullOrEmpty(copyTo) && !allowVendorAsset)
            {
                return new ErrorResponse("Ownership guard rejected direct authoring. Provide copy_to or explicitly set allow_vendor_asset=true.", new
                {
                    asset_path = assetPath,
                    ownership,
                    current_sha256 = currentSha256,
                    dry_run = dryRun,
                });
            }
            if (!string.IsNullOrEmpty(copyTo))
            {
                if (!RenderingAssetUtility.IsExactAssetPath(copyTo)
                    || !copyTo.StartsWith("Assets/", StringComparison.Ordinal)
                    || RenderingAssetUtility.ClassifyOwnership(copyTo).IsVendor)
                {
                    return new ErrorResponse("copy_to must be a project-owned path under Assets/.");
                }
                if (!string.Equals(Path.GetExtension(copyTo), Path.GetExtension(assetPath), StringComparison.OrdinalIgnoreCase))
                {
                    return new ErrorResponse("copy_to must preserve the source asset extension.");
                }
                if (AssetDatabase.LoadMainAssetAtPath(copyTo) != null || File.Exists(RenderingAssetUtility.GetFullPath(copyTo)))
                {
                    return new ErrorResponse($"copy_to already exists at '{copyTo}'. Refusing to overwrite it.");
                }
            }

            string effectivePath = string.IsNullOrEmpty(copyTo) ? assetPath : copyTo;
            List<object> manifest = new();
            manifest.Add(new
            {
                operation = string.IsNullOrEmpty(copyTo) ? "patch_in_place" : "copy_asset",
                source_path = assetPath,
                target_path = effectivePath,
                precondition_path = preconditionPath,
                expected_source_sha256 = currentSha256,
                ownership,
            });

            bool copyCreated = false;
            try
            {
                if (!dryRun && !string.IsNullOrEmpty(copyTo))
                {
                    if (!AssetDatabase.CopyAsset(assetPath, copyTo))
                    {
                        return new ErrorResponse($"Unity failed to copy '{assetPath}' to '{copyTo}'.");
                    }
                    copyCreated = true;
                    UnityEngine.Object created = AssetDatabase.LoadMainAssetAtPath(copyTo);
                    if (created != null)
                    {
                        Undo.RegisterCreatedObjectUndo(created, "Create rendering asset successor");
                    }
                }

                object result;
                switch (assetKind)
                {
                    case "material":
                    {
                        result = PatchMaterial(assetPath, effectivePath, operations, dryRun, manifest);
                        break;
                    }
                    case "texture_importer":
                    {
                        result = PatchTextureImporter(assetPath, effectivePath, operations, dryRun, manifest);
                        break;
                    }
                    case "shader_graph":
                    {
                        result = PatchShaderGraph(assetPath, effectivePath, operations, dryRun, manifest);
                        break;
                    }
                    default:
                    {
                        return new ErrorResponse($"Unsupported asset_kind '{assetKind}'.");
                    }
                }

                if (result is ErrorResponse && copyCreated)
                {
                    AssetDatabase.DeleteAsset(copyTo);
                    copyCreated = false;
                }
                return result;
            }
            catch (Exception exception)
            {
                if (copyCreated)
                {
                    AssetDatabase.DeleteAsset(copyTo);
                }
                return new ErrorResponse($"Rendering patch failed and created-copy rollback was attempted: {exception.Message}", new
                {
                    exception = exception.GetType().FullName,
                    asset_path = assetPath,
                    effective_asset_path = effectivePath,
                    copy_rolled_back = copyCreated,
                });
            }
        }

        private static object PatchMaterial(
            string sourcePath,
            string effectivePath,
            JArray operations,
            bool dryRun,
            List<object> manifest)
        {
            string loadPath = dryRun ? sourcePath : effectivePath;
            Material source = AssetDatabase.LoadAssetAtPath<Material>(loadPath);
            if (source == null)
            {
                return new ErrorResponse($"Could not load Material at '{loadPath}'.");
            }

            JObject before = SnapshotMaterial(source);
            Material planned = new(source) { hideFlags = HideFlags.HideAndDontSave };
            List<object> operationResults = new();
            bool changed;
            string error;
            try
            {
                changed = ApplyMaterialOperations(planned, operations, operationResults, out error);
                if (error != null)
                {
                    return new ErrorResponse(error, new { operation_results = operationResults });
                }
                JObject after = SnapshotMaterial(planned);
                manifest.Add(new
                {
                    operation = "material_patch",
                    asset_path = effectivePath,
                    changed,
                    operation_count = operations.Count,
                });

                if (!dryRun && changed)
                {
                    Undo.RecordObject(source, "Apply transactional rendering material patch");
                    List<object> appliedResults = new();
                    ApplyMaterialOperations(source, operations, appliedResults, out error);
                    if (error != null)
                    {
                        return new ErrorResponse($"Validated material patch failed during apply: {error}");
                    }
                    EditorUtility.SetDirty(source);
                    AssetDatabase.SaveAssetIfDirty(source);
                    AssetDatabase.ImportAsset(effectivePath, ImportAssetOptions.ForceSynchronousImport);
                }

                string postSha = dryRun
                    ? RenderingAssetUtility.ComputeSha256(sourcePath)
                    : RenderingAssetUtility.ComputeSha256(effectivePath);
                return new SuccessResponse(
                    dryRun ? "Material patch plan generated without mutation." : changed ? "Material patch applied." : "Material patch was idempotent; no material values changed.",
                    new
                    {
                        schema_version = "unity-mcp/rendering-authoring@1",
                        dry_run = dryRun,
                        asset_kind = "material",
                        source_asset_path = sourcePath,
                        effective_asset_path = effectivePath,
                        changed,
                        before,
                        after,
                        operation_results = operationResults,
                        mutation_manifest = manifest,
                        post_sha256 = postSha,
                        apply_precondition_path = RenderingAssetUtility.GetAuthoringPreconditionPath(sourcePath, "material"),
                        apply_precondition_sha256 = RenderingAssetUtility.ComputeAuthoringSha256(sourcePath, "material"),
                        import_requested = !dryRun && changed,
                        compilation_pending = EditorApplication.isCompiling,
                        undo_recorded = !dryRun && changed,
                    });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(planned);
            }
        }

        private static bool ApplyMaterialOperations(
            Material material,
            JArray operations,
            List<object> results,
            out string error)
        {
            bool changed = false;
            for (int index = 0; index < operations.Count; index++)
            {
                if (operations[index] is not JObject operation)
                {
                    error = $"Operation {index} must be an object.";
                    return false;
                }
                string action = operation["op"]?.ToString()?.Trim().ToLowerInvariant();
                string property = operation["property"]?.ToString();
                bool operationChanged = false;
                switch (action)
                {
                    case "set_float":
                    {
                        if (!RequireMaterialProperty(material, property, index, out error)) return false;
                        float value = operation["value"]?.Value<float>() ?? 0f;
                        operationChanged = !Mathf.Approximately(material.GetFloat(property), value);
                        if (operationChanged) material.SetFloat(property, value);
                        break;
                    }
                    case "set_int":
                    {
                        if (!RequireMaterialProperty(material, property, index, out error)) return false;
                        int value = operation["value"]?.Value<int>() ?? 0;
                        operationChanged = material.GetInteger(property) != value;
                        if (operationChanged) material.SetInteger(property, value);
                        break;
                    }
                    case "set_color":
                    {
                        if (!RequireMaterialProperty(material, property, index, out error)) return false;
                        if (!TryParseColor(operation["value"], out Color value))
                        {
                            error = $"Operation {index} set_color requires [r,g,b,a] or a color object.";
                            return false;
                        }
                        operationChanged = material.GetColor(property) != value;
                        if (operationChanged) material.SetColor(property, value);
                        break;
                    }
                    case "set_vector":
                    {
                        if (!RequireMaterialProperty(material, property, index, out error)) return false;
                        if (!TryParseVector(operation["value"], out Vector4 value))
                        {
                            error = $"Operation {index} set_vector requires [x,y,z,w] or a vector object.";
                            return false;
                        }
                        operationChanged = material.GetVector(property) != value;
                        if (operationChanged) material.SetVector(property, value);
                        break;
                    }
                    case "set_texture":
                    {
                        if (!RequireMaterialProperty(material, property, index, out error)) return false;
                        string texturePath = RenderingAssetUtility.NormalizeAssetPath(operation["texture_path"]?.ToString());
                        Texture value = string.IsNullOrEmpty(texturePath)
                            ? null
                            : AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                        if (!string.IsNullOrEmpty(texturePath) && value == null)
                        {
                            error = $"Operation {index} could not load texture '{texturePath}'.";
                            return false;
                        }
                        operationChanged = !ReferenceEquals(material.GetTexture(property), value);
                        if (operationChanged) material.SetTexture(property, value);
                        break;
                    }
                    case "set_texture_scale":
                    case "set_texture_offset":
                    {
                        if (!RequireMaterialProperty(material, property, index, out error)) return false;
                        if (!TryParseVector2(operation["value"], out Vector2 value))
                        {
                            error = $"Operation {index} {action} requires [x,y] or a vector object.";
                            return false;
                        }
                        Vector2 current = action == "set_texture_scale"
                            ? material.GetTextureScale(property)
                            : material.GetTextureOffset(property);
                        operationChanged = current != value;
                        if (operationChanged && action == "set_texture_scale") material.SetTextureScale(property, value);
                        if (operationChanged && action == "set_texture_offset") material.SetTextureOffset(property, value);
                        break;
                    }
                    case "set_keyword":
                    {
                        string keyword = operation["keyword"]?.ToString();
                        if (string.IsNullOrWhiteSpace(keyword))
                        {
                            error = $"Operation {index} set_keyword requires keyword.";
                            return false;
                        }
                        bool enabled = operation["enabled"]?.Value<bool>() ?? true;
                        bool current = material.IsKeywordEnabled(keyword);
                        operationChanged = current != enabled;
                        if (operationChanged && enabled) material.EnableKeyword(keyword);
                        if (operationChanged && !enabled) material.DisableKeyword(keyword);
                        break;
                    }
                    case "set_render_queue":
                    {
                        int value = operation["value"]?.Value<int>() ?? -1;
                        operationChanged = material.renderQueue != value;
                        if (operationChanged) material.renderQueue = value;
                        break;
                    }
                    case "set_instancing":
                    {
                        bool value = operation["value"]?.Value<bool>() ?? false;
                        operationChanged = material.enableInstancing != value;
                        if (operationChanged) material.enableInstancing = value;
                        break;
                    }
                    case "set_double_sided_gi":
                    {
                        bool value = operation["value"]?.Value<bool>() ?? false;
                        operationChanged = material.doubleSidedGI != value;
                        if (operationChanged) material.doubleSidedGI = value;
                        break;
                    }
                    default:
                    {
                        error = $"Operation {index} has unsupported material op '{action}'.";
                        return false;
                    }
                }
                changed |= operationChanged;
                results.Add(new { index, op = action, property, changed = operationChanged });
            }
            error = null;
            return changed;
        }

        private static object PatchTextureImporter(
            string sourcePath,
            string effectivePath,
            JArray operations,
            bool dryRun,
            List<object> manifest)
        {
            string loadPath = dryRun ? sourcePath : effectivePath;
            TextureImporter importer = AssetImporter.GetAtPath(loadPath) as TextureImporter;
            if (importer == null)
            {
                return new ErrorResponse($"Could not load TextureImporter at '{loadPath}'.");
            }
            JObject before = SnapshotTextureImporter(importer);
            JObject planned = (JObject)before.DeepClone();
            List<object> operationResults = new();
            bool changed = ApplyTextureImporterPlan(planned, operations, operationResults, out string error);
            if (error != null)
            {
                return new ErrorResponse(error, new { operation_results = operationResults });
            }
            manifest.Add(new
            {
                operation = "texture_importer_patch",
                asset_path = effectivePath,
                changed,
                operation_count = operations.Count,
            });

            if (!dryRun && changed)
            {
                Undo.RecordObject(importer, "Apply transactional texture importer patch");
                if (!ApplyTextureImporterValues(importer, planned, out error))
                {
                    return new ErrorResponse(error);
                }
                importer.SaveAndReimport();
                TextureImporter reloaded = AssetImporter.GetAtPath(effectivePath) as TextureImporter;
                JObject actual = reloaded == null ? null : SnapshotTextureImporter(reloaded);
                if (actual == null || !JToken.DeepEquals(planned, actual))
                {
                    if (reloaded != null && ApplyTextureImporterValues(reloaded, before, out _))
                    {
                        reloaded.SaveAndReimport();
                    }
                    return new ErrorResponse("Texture importer post-validation did not match the plan; original importer values were restored.", new
                    {
                        expected = planned,
                        actual,
                        rollback_requested = reloaded != null,
                    });
                }
            }
            string postSha = dryRun
                ? RenderingAssetUtility.ComputeAuthoringSha256(sourcePath, "texture_importer")
                : RenderingAssetUtility.ComputeAuthoringSha256(effectivePath, "texture_importer");
            return new SuccessResponse(
                dryRun ? "Texture importer patch plan generated without mutation." : changed ? "Texture importer patch applied." : "Texture importer patch was idempotent; no importer values changed.",
                new
                {
                    schema_version = "unity-mcp/rendering-authoring@1",
                    dry_run = dryRun,
                    asset_kind = "texture_importer",
                    source_asset_path = sourcePath,
                    effective_asset_path = effectivePath,
                    changed,
                    before,
                    after = planned,
                    operation_results = operationResults,
                    mutation_manifest = manifest,
                    post_sha256 = postSha,
                    apply_precondition_path = RenderingAssetUtility.GetAuthoringPreconditionPath(sourcePath, "texture_importer"),
                    apply_precondition_sha256 = RenderingAssetUtility.ComputeAuthoringSha256(sourcePath, "texture_importer"),
                    import_requested = !dryRun && changed,
                    compilation_pending = EditorApplication.isCompiling,
                    undo_recorded = !dryRun && changed,
                });
        }

        private static bool ApplyTextureImporterPlan(
            JObject planned,
            JArray operations,
            List<object> results,
            out string error)
        {
            HashSet<string> supported = new(StringComparer.OrdinalIgnoreCase)
            {
                "texture_type",
                "srgb",
                "mipmaps",
                "preserve_alpha_coverage",
                "alpha_test_reference",
                "wrap_mode",
                "filter_mode",
                "anisotropic_level",
                "compression",
                "compression_quality",
                "readable",
                "alpha_source",
            };
            bool changed = false;
            for (int index = 0; index < operations.Count; index++)
            {
                if (operations[index] is not JObject operation)
                {
                    error = $"Operation {index} must be an object.";
                    return false;
                }
                string action = operation["op"]?.ToString()?.Trim().ToLowerInvariant();
                if (action != "set")
                {
                    error = $"Texture importer operation {index} must use op='set'.";
                    return false;
                }
                string field = operation["field"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(field) || !supported.Contains(field))
                {
                    error = $"Texture importer operation {index} has unsupported field '{field}'.";
                    return false;
                }
                JToken value = operation["value"]?.DeepClone() ?? JValue.CreateNull();
                bool operationChanged = !JToken.DeepEquals(planned[field], value);
                if (operationChanged)
                {
                    planned[field] = value;
                }
                changed |= operationChanged;
                results.Add(new { index, op = action, field, changed = operationChanged });
            }
            error = null;
            return changed;
        }

        private static bool ApplyTextureImporterValues(TextureImporter importer, JObject values, out string error)
        {
            if (!TryParseEnum(values["texture_type"]?.ToString(), out TextureImporterType textureType)
                || !TryParseEnum(values["wrap_mode"]?.ToString(), out TextureWrapMode wrapMode)
                || !TryParseEnum(values["filter_mode"]?.ToString(), out FilterMode filterMode)
                || !TryParseEnum(values["compression"]?.ToString(), out TextureImporterCompression compression)
                || !TryParseEnum(values["alpha_source"]?.ToString(), out TextureImporterAlphaSource alphaSource))
            {
                error = "One or more planned texture importer enum values are invalid.";
                return false;
            }
            importer.textureType = textureType;
            importer.sRGBTexture = values["srgb"]?.Value<bool>() ?? importer.sRGBTexture;
            importer.mipmapEnabled = values["mipmaps"]?.Value<bool>() ?? importer.mipmapEnabled;
            importer.mipMapsPreserveCoverage = values["preserve_alpha_coverage"]?.Value<bool>() ?? importer.mipMapsPreserveCoverage;
            importer.alphaTestReferenceValue = values["alpha_test_reference"]?.Value<float>() ?? importer.alphaTestReferenceValue;
            importer.wrapMode = wrapMode;
            importer.filterMode = filterMode;
            importer.anisoLevel = values["anisotropic_level"]?.Value<int>() ?? importer.anisoLevel;
            importer.textureCompression = compression;
            importer.compressionQuality = values["compression_quality"]?.Value<int>() ?? importer.compressionQuality;
            importer.isReadable = values["readable"]?.Value<bool>() ?? importer.isReadable;
            importer.alphaSource = alphaSource;
            error = null;
            return true;
        }

        private static object PatchShaderGraph(
            string sourcePath,
            string effectivePath,
            JArray operations,
            bool dryRun,
            List<object> manifest)
        {
            string loadPath = dryRun ? sourcePath : effectivePath;
            string extension = Path.GetExtension(loadPath).ToLowerInvariant();
            if (extension != ".shadergraph" && extension != ".shadersubgraph")
            {
                return new ErrorResponse("shader_graph patches require a .shadergraph or .shadersubgraph asset.");
            }
            ShaderGraphDocumentFile graph = ShaderGraphDocumentFile.Load(RenderingAssetUtility.GetFullPath(loadPath));
            string[] beforeObjectIds = graph.Documents
                .Select(document => document.ObjectId)
                .Where(value => !string.IsNullOrEmpty(value))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            List<object> operationResults = new();
            bool changed = ApplyShaderGraphOperations(graph, operations, operationResults, out string error);
            if (error != null)
            {
                return new ErrorResponse(error, new { operation_results = operationResults });
            }
            byte[] plannedBytes = graph.Serialize();
            string plannedSha = RenderingAssetUtility.ComputeSha256(plannedBytes);
            string currentSha = RenderingAssetUtility.ComputeSha256(loadPath);
            changed = changed && !string.Equals(plannedSha, currentSha, StringComparison.OrdinalIgnoreCase);
            string[] afterObjectIds = graph.Documents
                .Select(document => document.ObjectId)
                .Where(value => !string.IsNullOrEmpty(value))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            manifest.Add(new
            {
                operation = "structured_shader_graph_patch",
                asset_path = effectivePath,
                changed,
                operation_count = operations.Count,
                modified_document_count = graph.Documents.Count(document => document.IsModified),
                added_object_ids = afterObjectIds.Except(beforeObjectIds, StringComparer.Ordinal).ToArray(),
                preserved_object_id_count = afterObjectIds.Intersect(beforeObjectIds, StringComparer.Ordinal).Count(),
            });

            if (!dryRun && changed)
            {
                string effectiveFullPath = RenderingAssetUtility.GetFullPath(effectivePath);
                byte[] originalBytes = File.ReadAllBytes(effectiveFullPath);
                WriteAtomically(effectiveFullPath, plannedBytes);
                AssetDatabase.ImportAsset(effectivePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                Shader importedShader = AssetDatabase.LoadAssetAtPath<Shader>(effectivePath);
                List<object> importedMessages = importedShader == null
                    ? new List<object>()
                    : GetCompilerMessages(importedShader);
                bool expectsImportedShader = string.Equals(
                    Path.GetExtension(effectivePath),
                    ".shadergraph",
                    StringComparison.OrdinalIgnoreCase);
                bool hasCompilerErrors = (expectsImportedShader && importedShader == null) || importedMessages.Any(message =>
                    string.Equals(JObject.FromObject(message)["severity"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase));
                try
                {
                    ShaderGraphDocumentFile importedGraph = ShaderGraphDocumentFile.Load(effectiveFullPath);
                    hasCompilerErrors |= importedGraph.FindGraphRoot() == null;
                }
                catch
                {
                    hasCompilerErrors = true;
                }
                if (hasCompilerErrors)
                {
                    WriteAtomically(effectiveFullPath, originalBytes);
                    AssetDatabase.ImportAsset(effectivePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    return new ErrorResponse("Shader Graph post-import validation failed; original bytes were restored.", new
                    {
                        effective_asset_path = effectivePath,
                        compiler_messages = importedMessages,
                        rollback_sha256 = RenderingAssetUtility.ComputeSha256(effectivePath),
                        rollback_requested = true,
                    });
                }
            }
            string postSha = dryRun ? currentSha : RenderingAssetUtility.ComputeSha256(effectivePath);
            Shader shader = dryRun ? null : AssetDatabase.LoadAssetAtPath<Shader>(effectivePath);
            List<object> compilerMessages = shader == null
                ? new List<object>()
                : GetCompilerMessages(shader);
            return new SuccessResponse(
                dryRun ? "Structured Shader Graph patch plan generated without mutation." : changed ? "Structured Shader Graph patch applied." : "Structured Shader Graph patch was idempotent; no bytes changed.",
                new
                {
                    schema_version = "unity-mcp/rendering-authoring@1",
                    dry_run = dryRun,
                    asset_kind = "shader_graph",
                    source_asset_path = sourcePath,
                    effective_asset_path = effectivePath,
                    changed,
                    current_sha256 = currentSha,
                    planned_sha256 = plannedSha,
                    post_sha256 = postSha,
                    before_object_ids = beforeObjectIds,
                    after_object_ids = afterObjectIds,
                    guid_and_version_fields_preserved_unless_explicitly_targeted = true,
                    operation_results = operationResults,
                    mutation_manifest = manifest,
                    compiler_messages = compilerMessages,
                    apply_precondition_path = RenderingAssetUtility.GetAuthoringPreconditionPath(sourcePath, "shader_graph"),
                    apply_precondition_sha256 = RenderingAssetUtility.ComputeAuthoringSha256(sourcePath, "shader_graph"),
                    import_requested = !dryRun && changed,
                    compilation_pending = EditorApplication.isCompiling,
                    undo_recorded = false,
                    undo_note = "Shader Graph file writes are SHA-guarded and atomic; Unity Undo does not own external file bytes.",
                });
        }

        private static bool ApplyShaderGraphOperations(
            ShaderGraphDocumentFile graph,
            JArray operations,
            List<object> results,
            out string error)
        {
            bool changed = false;
            for (int index = 0; index < operations.Count; index++)
            {
                if (operations[index] is not JObject operation)
                {
                    error = $"Operation {index} must be an object.";
                    return false;
                }
                string action = operation["op"]?.ToString()?.Trim().ToLowerInvariant();
                bool operationChanged;
                switch (action)
                {
                    case "set_field":
                    case "replace_subgraph_reference":
                    {
                        string objectId = operation["object_id"]?.ToString();
                        string path = operation["path"]?.ToString();
                        ShaderGraphDocument document = graph.FindByObjectId(objectId);
                        if (document == null)
                        {
                            error = $"Operation {index} could not find object_id '{objectId}'.";
                            return false;
                        }
                        JObject clone = (JObject)document.Value.DeepClone();
                        if (!RenderingAssetUtility.SetJsonPointer(clone, path, operation["value"], out error))
                        {
                            error = $"Operation {index}: {error}";
                            return false;
                        }
                        operationChanged = !JToken.DeepEquals(document.Value, clone);
                        if (operationChanged)
                        {
                            document.Value = clone;
                            document.IsModified = true;
                        }
                        break;
                    }
                    case "set_slot_value":
                    {
                        string objectId = operation["object_id"]?.ToString();
                        ShaderGraphDocument document = graph.FindByObjectId(objectId);
                        if (document == null)
                        {
                            error = $"Operation {index} could not find slot object_id '{objectId}'.";
                            return false;
                        }
                        JToken value = operation["value"]?.DeepClone() ?? JValue.CreateNull();
                        operationChanged = !JToken.DeepEquals(document.Value["m_Value"], value);
                        if (operationChanged)
                        {
                            document.Value["m_Value"] = value;
                            document.IsModified = true;
                        }
                        break;
                    }
                    case "set_property_flags":
                    {
                        string objectId = operation["object_id"]?.ToString();
                        ShaderGraphDocument document = graph.FindByObjectId(objectId);
                        JObject values = operation["values"] as JObject;
                        if (document == null || values == null)
                        {
                            error = $"Operation {index} requires a valid object_id and values object.";
                            return false;
                        }
                        operationChanged = false;
                        foreach (JProperty property in values.Properties())
                        {
                            if (!property.Name.StartsWith("m_", StringComparison.Ordinal))
                            {
                                error = $"Operation {index} flag '{property.Name}' must be an explicit serialized m_ field.";
                                return false;
                            }
                            if (!JToken.DeepEquals(document.Value[property.Name], property.Value))
                            {
                                document.Value[property.Name] = property.Value.DeepClone();
                                operationChanged = true;
                            }
                        }
                        document.IsModified |= operationChanged;
                        break;
                    }
                    case "connect_slots":
                    {
                        operationChanged = ConnectSlots(graph, operation, false, out error);
                        if (error != null)
                        {
                            error = $"Operation {index}: {error}";
                            return false;
                        }
                        break;
                    }
                    case "disconnect_slots":
                    {
                        operationChanged = ConnectSlots(graph, operation, true, out error);
                        if (error != null)
                        {
                            error = $"Operation {index}: {error}";
                            return false;
                        }
                        break;
                    }
                    case "add_node":
                    {
                        operationChanged = AddNode(graph, operation, out error);
                        if (error != null)
                        {
                            error = $"Operation {index}: {error}";
                            return false;
                        }
                        break;
                    }
                    default:
                    {
                        error = $"Operation {index} has unsupported Shader Graph op '{action}'.";
                        return false;
                    }
                }
                changed |= operationChanged;
                results.Add(new { index, op = action, changed = operationChanged });
            }
            error = null;
            return changed;
        }

        private static bool ConnectSlots(
            ShaderGraphDocumentFile graph,
            JObject operation,
            bool disconnect,
            out string error)
        {
            ShaderGraphDocument root = graph.FindGraphRoot();
            if (root == null)
            {
                error = "Graph root with m_Edges was not found.";
                return false;
            }
            string outputNode = operation["output_node_id"]?.ToString();
            int? outputSlot = operation["output_slot_id"]?.Value<int?>();
            string inputNode = operation["input_node_id"]?.ToString();
            int? inputSlot = operation["input_slot_id"]?.Value<int?>();
            if (string.IsNullOrEmpty(outputNode) || !outputSlot.HasValue
                || string.IsNullOrEmpty(inputNode) || !inputSlot.HasValue)
            {
                error = "connect/disconnect_slots requires output_node_id, output_slot_id, input_node_id, and input_slot_id.";
                return false;
            }
            JArray edges = root.Value["m_Edges"] as JArray;
            if (edges == null)
            {
                edges = new JArray();
                root.Value["m_Edges"] = edges;
            }
            List<JToken> matches = edges.Where(edge =>
                edge["m_OutputSlot"]?["m_Node"]?["m_Id"]?.ToString() == outputNode
                && edge["m_OutputSlot"]?["m_SlotId"]?.Value<int?>() == outputSlot
                && edge["m_InputSlot"]?["m_Node"]?["m_Id"]?.ToString() == inputNode
                && edge["m_InputSlot"]?["m_SlotId"]?.Value<int?>() == inputSlot).ToList();
            if (disconnect)
            {
                foreach (JToken match in matches)
                {
                    match.Remove();
                }
                root.IsModified |= matches.Count > 0;
                error = null;
                return matches.Count > 0;
            }
            if (matches.Count > 0)
            {
                error = null;
                return false;
            }
            edges.Add(new JObject
            {
                ["m_OutputSlot"] = new JObject
                {
                    ["m_Node"] = new JObject { ["m_Id"] = outputNode },
                    ["m_SlotId"] = outputSlot.Value,
                },
                ["m_InputSlot"] = new JObject
                {
                    ["m_Node"] = new JObject { ["m_Id"] = inputNode },
                    ["m_SlotId"] = inputSlot.Value,
                },
            });
            root.IsModified = true;
            error = null;
            return true;
        }

        private static bool AddNode(
            ShaderGraphDocumentFile graph,
            JObject operation,
            out string error)
        {
            string nodeId = operation["node_id"]?.ToString();
            JArray documents = operation["documents"] as JArray;
            ShaderGraphDocument root = graph.FindGraphRoot();
            if (string.IsNullOrEmpty(nodeId) || documents == null || documents.Count == 0 || root == null)
            {
                error = "add_node requires node_id, a non-empty documents array, and a graph root.";
                return false;
            }
            if (graph.FindByObjectId(nodeId) != null)
            {
                error = null;
                return false;
            }
            List<JObject> parsed = documents.OfType<JObject>().Select(value => (JObject)value.DeepClone()).ToList();
            if (parsed.Count != documents.Count || parsed.All(value => value["m_ObjectId"]?.ToString() != nodeId))
            {
                error = "add_node documents must be objects and include the declared node_id.";
                return false;
            }
            HashSet<string> existingIds = graph.Documents
                .Select(document => document.ObjectId)
                .Where(value => !string.IsNullOrEmpty(value))
                .ToHashSet(StringComparer.Ordinal);
            foreach (JObject document in parsed)
            {
                string objectId = document["m_ObjectId"]?.ToString();
                if (string.IsNullOrEmpty(objectId) || existingIds.Contains(objectId))
                {
                    error = $"add_node document has missing or duplicate m_ObjectId '{objectId}'.";
                    return false;
                }
                existingIds.Add(objectId);
            }
            JArray nodes = root.Value["m_Nodes"] as JArray;
            if (nodes == null)
            {
                nodes = new JArray();
                root.Value["m_Nodes"] = nodes;
            }
            nodes.Add(new JObject { ["m_Id"] = nodeId });
            root.IsModified = true;
            foreach (JObject document in parsed)
            {
                graph.AddDocument(document);
            }
            error = null;
            return true;
        }

        private static JObject SnapshotMaterial(Material material)
        {
            JObject properties = new();
            Shader shader = material.shader;
            int count = shader == null ? 0 : shader.GetPropertyCount();
            for (int index = 0; index < count; index++)
            {
                string name = shader.GetPropertyName(index);
                switch (shader.GetPropertyType(index))
                {
                    case ShaderPropertyType.Color:
                    {
                        Color value = material.GetColor(name);
                        properties[name] = new JArray(value.r, value.g, value.b, value.a);
                        break;
                    }
                    case ShaderPropertyType.Vector:
                    {
                        Vector4 value = material.GetVector(name);
                        properties[name] = new JArray(value.x, value.y, value.z, value.w);
                        break;
                    }
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        properties[name] = material.GetFloat(name);
                        break;
                    }
                    case ShaderPropertyType.Int:
                    {
                        properties[name] = material.GetInteger(name);
                        break;
                    }
                    case ShaderPropertyType.Texture:
                    {
                        Texture texture = material.GetTexture(name);
                        properties[name] = new JObject
                        {
                            ["path"] = texture == null ? null : AssetDatabase.GetAssetPath(texture),
                            ["guid"] = texture == null ? null : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(texture)),
                            ["scale"] = Vector2Token(material.GetTextureScale(name)),
                            ["offset"] = Vector2Token(material.GetTextureOffset(name)),
                        };
                        break;
                    }
                }
            }
            return new JObject
            {
                ["shader_path"] = shader == null ? null : AssetDatabase.GetAssetPath(shader),
                ["shader_guid"] = shader == null ? null : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(shader)),
                ["properties"] = properties,
                ["keywords"] = new JArray(material.shaderKeywords.OrderBy(value => value, StringComparer.Ordinal)),
                ["render_queue"] = material.renderQueue,
                ["instancing"] = material.enableInstancing,
                ["double_sided_gi"] = material.doubleSidedGI,
            };
        }

        private static JObject SnapshotTextureImporter(TextureImporter importer)
        {
            return new JObject
            {
                ["texture_type"] = importer.textureType.ToString(),
                ["srgb"] = importer.sRGBTexture,
                ["mipmaps"] = importer.mipmapEnabled,
                ["preserve_alpha_coverage"] = importer.mipMapsPreserveCoverage,
                ["alpha_test_reference"] = importer.alphaTestReferenceValue,
                ["wrap_mode"] = importer.wrapMode.ToString(),
                ["filter_mode"] = importer.filterMode.ToString(),
                ["anisotropic_level"] = importer.anisoLevel,
                ["compression"] = importer.textureCompression.ToString(),
                ["compression_quality"] = importer.compressionQuality,
                ["readable"] = importer.isReadable,
                ["alpha_source"] = importer.alphaSource.ToString(),
            };
        }

        private static bool RequireMaterialProperty(
            Material material,
            string property,
            int index,
            out string error)
        {
            if (string.IsNullOrWhiteSpace(property) || !material.HasProperty(property))
            {
                error = $"Operation {index} references missing material property '{property}'.";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryParseColor(JToken token, out Color value)
        {
            if (token is JArray array && array.Count >= 3)
            {
                value = new Color(
                    array[0].Value<float>(),
                    array[1].Value<float>(),
                    array[2].Value<float>(),
                    array.Count >= 4 ? array[3].Value<float>() : 1f);
                return true;
            }
            if (token is JObject obj)
            {
                value = new Color(
                    obj["r"]?.Value<float>() ?? 0f,
                    obj["g"]?.Value<float>() ?? 0f,
                    obj["b"]?.Value<float>() ?? 0f,
                    obj["a"]?.Value<float>() ?? 1f);
                return true;
            }
            value = default;
            return false;
        }

        private static JArray Vector2Token(Vector2 value)
        {
            return new JArray(value.x, value.y);
        }

        private static bool TryParseVector(JToken token, out Vector4 value)
        {
            if (token is JArray array && array.Count >= 4)
            {
                value = new Vector4(
                    array[0].Value<float>(),
                    array[1].Value<float>(),
                    array[2].Value<float>(),
                    array[3].Value<float>());
                return true;
            }
            if (token is JObject obj)
            {
                value = new Vector4(
                    obj["x"]?.Value<float>() ?? 0f,
                    obj["y"]?.Value<float>() ?? 0f,
                    obj["z"]?.Value<float>() ?? 0f,
                    obj["w"]?.Value<float>() ?? 0f);
                return true;
            }
            value = default;
            return false;
        }

        private static bool TryParseVector2(JToken token, out Vector2 value)
        {
            if (token is JArray array && array.Count >= 2)
            {
                value = new Vector2(array[0].Value<float>(), array[1].Value<float>());
                return true;
            }
            if (token is JObject obj)
            {
                value = new Vector2(
                    obj["x"]?.Value<float>() ?? 0f,
                    obj["y"]?.Value<float>() ?? 0f);
                return true;
            }
            value = default;
            return false;
        }

        private static bool TryParseEnum<T>(string value, out T parsed) where T : struct
        {
            return Enum.TryParse(value, true, out parsed);
        }

        private static void WriteAtomically(string fullPath, byte[] content)
        {
            string temporaryPath = $"{fullPath}.mcp-tmp-{Guid.NewGuid():N}";
            File.WriteAllBytes(temporaryPath, content);
            try
            {
                File.Replace(temporaryPath, fullPath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temporaryPath, fullPath, true);
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                File.Copy(temporaryPath, fullPath, true);
                File.Delete(temporaryPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static List<object> GetCompilerMessages(Shader shader)
        {
            List<object> messages = new();
            Array rawMessages = ShaderUtil.GetShaderMessages(shader);
            foreach (object raw in rawMessages)
            {
                Type type = raw.GetType();
                System.Reflection.FieldInfo messageField = type.GetField("message");
                System.Reflection.FieldInfo severityField = type.GetField("severity");
                messages.Add(new
                {
                    message = messageField?.GetValue(raw)?.ToString(),
                    severity = severityField?.GetValue(raw)?.ToString(),
                });
            }
            return messages;
        }
    }
}
