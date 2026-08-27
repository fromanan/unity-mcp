using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.Rendering
{
    internal sealed class RenderingOwnershipInfo
    {
        public string Owner { get; set; }
        public string AssetClass { get; set; }
        public bool IsVendor { get; set; }
        public bool IsGenerated { get; set; }
        public bool RequiresProjectCopy { get; set; }
        public bool RequiresSuccessorManifest { get; set; }
        public string Reason { get; set; }
    }

    internal sealed class TextureSemanticContract
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public bool? ExpectedSrgb { get; set; }
        public string ExpectedImporterType { get; set; }
        public bool? ExpectedMipmaps { get; set; }
        public Dictionary<string, string> Channels { get; set; } = new();
        public bool IsKnown => !string.Equals(Name, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class ShaderGraphDocument
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public JObject Value { get; set; }
        public bool IsModified { get; set; }

        public string ObjectId => Value?["m_ObjectId"]?.ToString();
        public string TypeName => Value?["m_Type"]?.ToString();
    }

    internal sealed class ShaderGraphDocumentFile
    {
        private readonly string _source;
        private readonly List<ShaderGraphDocument> _documents;

        public string Newline { get; }
        public bool HasUtf8Bom { get; }
        public IReadOnlyList<ShaderGraphDocument> Documents => _documents;

        private ShaderGraphDocumentFile(
            string source,
            List<ShaderGraphDocument> documents,
            string newline,
            bool hasUtf8Bom)
        {
            _source = source;
            _documents = documents;
            Newline = newline;
            HasUtf8Bom = hasUtf8Bom;
        }

        public static ShaderGraphDocumentFile Load(string fullPath)
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            bool hasBom = bytes.Length >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF;
            int offset = hasBom ? 3 : 0;
            string source = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
            string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            List<ShaderGraphDocument> documents = ParseDocuments(source);
            return new ShaderGraphDocumentFile(source, documents, newline, hasBom);
        }

        public ShaderGraphDocument FindByObjectId(string objectId)
        {
            return _documents.FirstOrDefault(document =>
                string.Equals(document.ObjectId, objectId, StringComparison.Ordinal));
        }

        public ShaderGraphDocument FindGraphRoot()
        {
            return _documents.FirstOrDefault(document => document.Value?["m_Edges"] is JArray)
                ?? _documents.FirstOrDefault(document =>
                    document.TypeName?.Contains("GraphData", StringComparison.OrdinalIgnoreCase) == true);
        }

        public void AddDocument(JObject value)
        {
            _documents.Add(new ShaderGraphDocument
            {
                Start = _source.Length,
                Length = 0,
                Value = value,
                IsModified = true,
            });
        }

        public bool RemoveDocument(string objectId)
        {
            ShaderGraphDocument document = FindByObjectId(objectId);
            return document != null && _documents.Remove(document);
        }

        public byte[] Serialize()
        {
            StringBuilder builder = new(_source);
            List<ShaderGraphDocument> existingModified = _documents
                .Where(document => document.IsModified && document.Length > 0)
                .OrderByDescending(document => document.Start)
                .ToList();

            foreach (ShaderGraphDocument document in existingModified)
            {
                string replacement = SerializeDocument(document.Value);
                builder.Remove(document.Start, document.Length);
                builder.Insert(document.Start, replacement);
            }

            List<ShaderGraphDocument> additions = _documents
                .Where(document => document.Length == 0)
                .ToList();
            foreach (ShaderGraphDocument addition in additions)
            {
                if (builder.Length > 0 && !builder.ToString().EndsWith(Newline, StringComparison.Ordinal))
                {
                    builder.Append(Newline);
                }
                builder.Append(SerializeDocument(addition.Value));
                builder.Append(Newline);
            }

            byte[] content = new UTF8Encoding(false).GetBytes(builder.ToString());
            if (!HasUtf8Bom)
            {
                return content;
            }

            byte[] result = new byte[content.Length + 3];
            result[0] = 0xEF;
            result[1] = 0xBB;
            result[2] = 0xBF;
            Buffer.BlockCopy(content, 0, result, 3, content.Length);
            return result;
        }

        private string SerializeDocument(JObject value)
        {
            string serialized = value.ToString(Formatting.Indented);
            return Newline == "\n"
                ? serialized.Replace("\r\n", "\n")
                : serialized.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        private static List<ShaderGraphDocument> ParseDocuments(string source)
        {
            List<ShaderGraphDocument> documents = new();
            int depth = 0;
            int start = -1;
            bool insideString = false;
            bool escaped = false;

            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];
                if (insideString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        insideString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    insideString = true;
                    continue;
                }

                if (character == '{')
                {
                    if (depth == 0)
                    {
                        start = index;
                    }
                    depth++;
                    continue;
                }

                if (character != '}')
                {
                    continue;
                }

                depth--;
                if (depth < 0)
                {
                    throw new JsonReaderException("Shader Graph contains an unmatched closing brace.");
                }
                if (depth != 0 || start < 0)
                {
                    continue;
                }

                int length = index - start + 1;
                string documentText = source.Substring(start, length);
                JObject value = JObject.Parse(documentText);
                documents.Add(new ShaderGraphDocument
                {
                    Start = start,
                    Length = length,
                    Value = value,
                    IsModified = false,
                });
                start = -1;
            }

            if (insideString || depth != 0)
            {
                throw new JsonReaderException("Shader Graph contains an incomplete JSON document.");
            }
            if (documents.Count == 0)
            {
                throw new JsonReaderException("Shader Graph contains no JSON documents.");
            }
            return documents;
        }
    }

    internal static class RenderingAssetUtility
    {
        internal const string ContractRegistryVersion = "zornhau-render-contracts@1";

        internal static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            string normalized = path.Trim().Replace('\\', '/');
            while (normalized.Contains("//", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("//", "/");
            }
            return normalized;
        }

        internal static bool IsExactAssetPath(string path)
        {
            string normalized = NormalizeAssetPath(path);
            return normalized != null
                && !normalized.Contains("..", StringComparison.Ordinal)
                && (normalized.StartsWith("Assets/", StringComparison.Ordinal)
                    || normalized.StartsWith("Packages/", StringComparison.Ordinal));
        }

        internal static string GetFullPath(string assetPath)
        {
            string normalized = NormalizeAssetPath(assetPath);
            if (!IsExactAssetPath(normalized))
            {
                return null;
            }
            if (normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                UnityEditor.PackageManager.PackageInfo package =
                    UnityEditor.PackageManager.PackageInfo.FindForAssetPath(normalized);
                if (package == null || string.IsNullOrEmpty(package.resolvedPath))
                {
                    return null;
                }
                string packageAssetRoot = $"Packages/{package.name}";
                if (!normalized.Equals(packageAssetRoot, StringComparison.Ordinal)
                    && !normalized.StartsWith(packageAssetRoot + "/", StringComparison.Ordinal))
                {
                    return null;
                }
                string relative = normalized.Length == packageAssetRoot.Length
                    ? string.Empty
                    : normalized.Substring(packageAssetRoot.Length + 1);
                string resolvedRoot = Path.GetFullPath(package.resolvedPath);
                string packagePath = Path.GetFullPath(Path.Combine(
                    resolvedRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                string packagePrefix = resolvedRoot.TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return packagePath.Equals(resolvedRoot, StringComparison.OrdinalIgnoreCase)
                    || packagePath.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase)
                        ? packagePath
                        : null;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string fullPath = Path.GetFullPath(Path.Combine(
                projectRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            string rootPrefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }

        internal static bool TryResolveOutputPath(
            string requestedPath,
            out string projectRelativePath,
            out string fullPath,
            out string error)
        {
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string normalized = string.IsNullOrWhiteSpace(requestedPath)
                ? $"Library/MCPForUnity/RenderProbes/render-probe-{timestamp}.png"
                : requestedPath.Trim().Replace('\\', '/');
            if (normalized.Contains("..", StringComparison.Ordinal)
                || !(normalized.StartsWith("Assets/", StringComparison.Ordinal)
                    || normalized.StartsWith("Library/MCPForUnity/RenderProbes/", StringComparison.Ordinal)))
            {
                projectRelativePath = null;
                fullPath = null;
                error = "output_path must be under Assets/ or Library/MCPForUnity/RenderProbes/.";
                return false;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string candidate = Path.GetFullPath(Path.Combine(
                projectRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            string rootPrefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                projectRelativePath = null;
                fullPath = null;
                error = "output_path resolves outside the Unity project.";
                return false;
            }

            projectRelativePath = normalized;
            fullPath = candidate;
            error = null;
            return true;
        }

        internal static string ComputeSha256(string assetPath)
        {
            string fullPath = GetFullPath(assetPath);
            return ComputeFileSha256(fullPath);
        }

        internal static string ComputeAuthoringSha256(string assetPath, string assetKind)
        {
            string fullPath = GetFullPath(assetPath);
            if (string.Equals(assetKind, "texture_importer", StringComparison.OrdinalIgnoreCase))
            {
                fullPath = fullPath == null ? null : fullPath + ".meta";
            }
            return ComputeFileSha256(fullPath);
        }

        internal static string GetAuthoringPreconditionPath(string assetPath, string assetKind)
        {
            return string.Equals(assetKind, "texture_importer", StringComparison.OrdinalIgnoreCase)
                ? NormalizeAssetPath(assetPath) + ".meta"
                : NormalizeAssetPath(assetPath);
        }

        private static string ComputeFileSha256(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }
            using SHA256 sha = SHA256.Create();
            using FileStream stream = File.OpenRead(fullPath);
            return ToLowerHex(sha.ComputeHash(stream));
        }

        internal static string ComputeSha256(byte[] content)
        {
            using SHA256 sha = SHA256.Create();
            return ToLowerHex(sha.ComputeHash(content));
        }

        private static string ToLowerHex(byte[] bytes)
        {
            StringBuilder builder = new(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString("x2"));
            }
            return builder.ToString();
        }

        internal static RenderingOwnershipInfo ClassifyOwnership(string assetPath)
        {
            string path = NormalizeAssetPath(assetPath) ?? string.Empty;
            string lower = path.ToLowerInvariant();
            RenderingOwnershipInfo result = new()
            {
                Owner = "project",
                AssetClass = "project_asset",
                IsVendor = false,
                IsGenerated = false,
                RequiresProjectCopy = false,
                RequiresSuccessorManifest = false,
                Reason = "Asset is under a project-owned path with no known generated/vendor marker.",
            };

            if (lower.StartsWith("packages/", StringComparison.Ordinal))
            {
                result.Owner = "package";
                result.AssetClass = "package_payload";
                result.IsVendor = true;
                result.RequiresProjectCopy = true;
                result.RequiresSuccessorManifest = true;
                result.Reason = "UPM package payloads require a project-owned successor before authoring.";
                return result;
            }

            if (lower.StartsWith("assets/freshcan3d/", StringComparison.Ordinal))
            {
                result.Owner = "FreshCan3D";
                result.AssetClass = "vendor_source";
                result.IsVendor = true;
                result.RequiresProjectCopy = true;
                result.RequiresSuccessorManifest = true;
                result.Reason = "FreshCan source is vendor-owned; preserve GUID/channel contracts and author a project copy.";
                return result;
            }

            if (lower.Contains("the vegetation engine", StringComparison.Ordinal)
                || lower.Contains("/boxophobic/", StringComparison.Ordinal)
                || lower.Contains("/tve/", StringComparison.Ordinal))
            {
                result.Owner = "The Visual Engine";
                result.AssetClass = "vendor_or_generated_vegetation";
                result.IsVendor = true;
                result.IsGenerated = lower.Contains("generated", StringComparison.Ordinal);
                result.RequiresProjectCopy = true;
                result.RequiresSuccessorManifest = true;
                result.Reason = "TVE source/generated outputs have package-specific ownership and regeneration rules.";
                return result;
            }

            if (lower.Contains("microsplat", StringComparison.Ordinal))
            {
                result.Owner = "MicroSplat";
                result.AssetClass = "generated_terrain_rendering";
                result.IsGenerated = true;
                result.RequiresProjectCopy = false;
                result.RequiresSuccessorManifest = true;
                result.Reason = "MicroSplat shaders/property data are generator-owned and must be changed through their owning workflow.";
                return result;
            }

            if (lower.Contains("bakerylightmap", StringComparison.Ordinal)
                || lower.Contains("/bakery/generated", StringComparison.Ordinal)
                || lower.Contains("_bakery", StringComparison.Ordinal))
            {
                result.Owner = "Bakery";
                result.AssetClass = "generated_lighting";
                result.IsGenerated = true;
                result.RequiresSuccessorManifest = true;
                result.Reason = "Bakery output is generated lighting data and must remain owned by the bake workflow.";
                return result;
            }

            if (lower.Contains("/terrainData/".ToLowerInvariant(), StringComparison.Ordinal)
                || lower.StartsWith("assets/shaders/generated/", StringComparison.Ordinal)
                || lower.StartsWith("assets/_generated/", StringComparison.Ordinal))
            {
                result.Owner = "project_generator";
                result.AssetClass = "generated_project_asset";
                result.IsGenerated = true;
                result.RequiresSuccessorManifest = true;
                result.Reason = "Generated project assets require an owning manifest/tool and idempotent regeneration.";
            }
            return result;
        }

        internal static TextureSemanticContract ClassifyTextureContract(
            string assetPath,
            string requestedContract)
        {
            string requested = requestedContract?.Trim().ToLowerInvariant();
            string normalized = NormalizeAssetPath(assetPath) ?? string.Empty;
            string lower = normalized.ToLowerInvariant();
            string selected = requested;

            if (string.IsNullOrEmpty(selected))
            {
                if (lower.Contains("_n_ao_r", StringComparison.Ordinal))
                {
                    selected = "freshcan_n_ao_r";
                }
                else if (lower.Contains("generatedmasks/freshcan", StringComparison.Ordinal)
                    || lower.Contains("_metallicgloss", StringComparison.Ordinal)
                    || lower.Contains("_mask", StringComparison.Ordinal))
                {
                    selected = "urp_mask";
                }
                else if (lower.Contains("normal", StringComparison.Ordinal)
                    || lower.EndsWith("_n.png", StringComparison.Ordinal)
                    || lower.EndsWith("_n.tga", StringComparison.Ordinal))
                {
                    selected = "normal";
                }
                else
                {
                    selected = "unknown";
                }
            }

            switch (selected)
            {
                case "freshcan_n_ao_r":
                {
                    return new TextureSemanticContract
                    {
                        Name = "freshcan_n_ao_r",
                        Source = string.IsNullOrEmpty(requested) ? "path_registry" : "caller",
                        ExpectedSrgb = false,
                        ExpectedImporterType = "Default",
                        ExpectedMipmaps = true,
                        Channels = new Dictionary<string, string>
                        {
                            ["r"] = "tangent_normal_x",
                            ["g"] = "tangent_normal_y",
                            ["b"] = "ambient_occlusion",
                            ["a"] = "roughness",
                        },
                    };
                }
                case "urp_mask":
                {
                    return new TextureSemanticContract
                    {
                        Name = "urp_mask",
                        Source = string.IsNullOrEmpty(requested) ? "path_registry" : "caller",
                        ExpectedSrgb = false,
                        ExpectedImporterType = "Default",
                        ExpectedMipmaps = true,
                        Channels = new Dictionary<string, string>
                        {
                            ["r"] = "metallic",
                            ["g"] = "ambient_occlusion",
                            ["b"] = "unused_or_workflow_declared_height",
                            ["a"] = "smoothness",
                        },
                    };
                }
                case "normal":
                {
                    return new TextureSemanticContract
                    {
                        Name = "normal",
                        Source = string.IsNullOrEmpty(requested) ? "path_registry" : "caller",
                        ExpectedSrgb = false,
                        ExpectedImporterType = "NormalMap",
                        ExpectedMipmaps = true,
                        Channels = new Dictionary<string, string>
                        {
                            ["rgb"] = "tangent_normal",
                            ["a"] = "workflow_specific",
                        },
                    };
                }
                case "color":
                {
                    return new TextureSemanticContract
                    {
                        Name = "color",
                        Source = "caller",
                        ExpectedSrgb = true,
                        ExpectedImporterType = "Default",
                        ExpectedMipmaps = true,
                        Channels = new Dictionary<string, string>
                        {
                            ["rgb"] = "color",
                            ["a"] = "opacity_or_workflow_specific",
                        },
                    };
                }
                default:
                {
                    return new TextureSemanticContract
                    {
                        Name = "unknown",
                        Source = string.IsNullOrEmpty(requested) ? "unclassified" : "unsupported_caller_contract",
                    };
                }
            }
        }

        internal static bool SetJsonPointer(JObject document, string pointer, JToken value, out string error)
        {
            if (document == null || string.IsNullOrWhiteSpace(pointer) || !pointer.StartsWith("/", StringComparison.Ordinal))
            {
                error = "set_field requires a JSON Pointer path beginning with '/'.";
                return false;
            }

            string[] segments = pointer.Split('/').Skip(1)
                .Select(segment => segment.Replace("~1", "/").Replace("~0", "~"))
                .ToArray();
            if (segments.Length == 0)
            {
                error = "The root document cannot be replaced by set_field.";
                return false;
            }

            JToken current = document;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                string segment = segments[index];
                if (current is JObject currentObject)
                {
                    JToken next = currentObject[segment];
                    if (next == null)
                    {
                        next = new JObject();
                        currentObject[segment] = next;
                    }
                    current = next;
                    continue;
                }

                if (current is JArray currentArray
                    && int.TryParse(segment, out int arrayIndex)
                    && arrayIndex >= 0
                    && arrayIndex < currentArray.Count)
                {
                    current = currentArray[arrayIndex];
                    continue;
                }

                error = $"JSON Pointer segment '{segment}' does not resolve to an object or array element.";
                return false;
            }

            string finalSegment = segments[^1];
            if (current is JObject finalObject)
            {
                finalObject[finalSegment] = value?.DeepClone() ?? JValue.CreateNull();
                error = null;
                return true;
            }
            if (current is JArray finalArray
                && int.TryParse(finalSegment, out int finalIndex)
                && finalIndex >= 0
                && finalIndex < finalArray.Count)
            {
                finalArray[finalIndex] = value?.DeepClone() ?? JValue.CreateNull();
                error = null;
                return true;
            }

            error = $"Final JSON Pointer segment '{finalSegment}' is not writable.";
            return false;
        }

        internal static string GetHierarchyPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }
            List<string> segments = new();
            Transform current = gameObject.transform;
            while (current != null)
            {
                segments.Add(current.name);
                current = current.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        internal static GameObject ResolveGameObject(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }
            if (int.TryParse(target, out int instanceId))
            {
                return UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>()
                    .FirstOrDefault(gameObject => gameObject.GetInstanceID() == instanceId);
            }

            GameObject direct = GameObject.Find(target);
            if (direct != null)
            {
                return direct;
            }

            GameObject[] all = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>();
            return all.FirstOrDefault(gameObject =>
                !EditorUtility.IsPersistent(gameObject)
                && (string.Equals(gameObject.name, target, StringComparison.Ordinal)
                    || string.Equals(GetHierarchyPath(gameObject), target, StringComparison.Ordinal)));
        }
    }
}
