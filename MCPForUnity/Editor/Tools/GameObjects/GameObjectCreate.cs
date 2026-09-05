#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Security;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.GameObjects
{
    internal static class GameObjectCreate
    {
        private const int MaxCapturedLogs = 8;
        private const int MaxCapturedLogCharacters = 512;

        private enum PrefabInstancePolicy
        {
            AlwaysCreate,
            FailIfSamePrefab,
            ReuseSamePrefab
        }

        private sealed class ScopedLogCollector : IDisposable
        {
            private readonly List<Dictionary<string, string>> _entries = new();

            internal ScopedLogCollector()
            {
                Application.logMessageReceived += OnLogMessageReceived;
            }

            internal IReadOnlyList<Dictionary<string, string>> Snapshot()
            {
                return _entries.ToArray();
            }

            public void Dispose()
            {
                Application.logMessageReceived -= OnLogMessageReceived;
            }

            private void OnLogMessageReceived(string condition, string stackTrace, LogType type)
            {
                if (_entries.Count >= MaxCapturedLogs)
                {
                    return;
                }

                string message = SecretRedactor.Scrub(condition ?? string.Empty)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Trim();
                if (message.Length > MaxCapturedLogCharacters)
                {
                    message = message.Substring(0, MaxCapturedLogCharacters) + "...";
                }

                _entries.Add(new Dictionary<string, string>
                {
                    ["type"] = type.ToString(),
                    ["message"] = message
                });
            }
        }

        internal static bool IsPlayModePrefabCreationBlocked(bool isPlaying, bool allowPlayModeCreate)
        {
            return isPlaying && !allowPlayModeCreate;
        }

        private static bool TryReadOptionalBoolean(JObject @params, string parameterName, bool defaultValue, out bool value)
        {
            JToken token = @params[parameterName];
            if (token == null || token.Type == JTokenType.Null)
            {
                value = defaultValue;
                return true;
            }

            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }

            if (token.Type == JTokenType.String && bool.TryParse(token.ToString(), out bool parsed))
            {
                value = parsed;
                return true;
            }

            value = defaultValue;
            return false;
        }

        private static bool TryParseInstancePolicy(string rawPolicy, out PrefabInstancePolicy policy)
        {
            switch ((rawPolicy ?? "always_create").Trim().ToLowerInvariant())
            {
                case "always_create":
                {
                    policy = PrefabInstancePolicy.AlwaysCreate;
                    return true;
                }
                case "fail_if_same_prefab":
                {
                    policy = PrefabInstancePolicy.FailIfSamePrefab;
                    return true;
                }
                case "reuse_same_prefab":
                {
                    policy = PrefabInstancePolicy.ReuseSamePrefab;
                    return true;
                }
                default:
                {
                    policy = PrefabInstancePolicy.AlwaysCreate;
                    return false;
                }
            }
        }

        private static List<GameObject> FindExistingPrefabInstances(GameObject prefabAsset, string prefabPath)
        {
            string prefabAssetName = prefabAsset.name;
            string cloneName = prefabAssetName + "(Clone)";
            List<GameObject> matches = new();

            foreach (GameObject candidate in UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate == null || EditorUtility.IsPersistent(candidate) || candidate.transform.parent != null)
                {
                    continue;
                }

                if (!candidate.scene.IsValid() || !candidate.scene.isLoaded || EditorSceneManager.IsPreviewScene(candidate.scene))
                {
                    continue;
                }

                UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(candidate);
                bool sourceMatches = source != null &&
                    string.Equals(AssetDatabase.GetAssetPath(source), prefabPath, StringComparison.OrdinalIgnoreCase);
                bool playModeNameMatches = EditorApplication.isPlaying &&
                    (string.Equals(candidate.name, prefabAssetName, StringComparison.Ordinal) ||
                     string.Equals(candidate.name, cloneName, StringComparison.Ordinal));

                if (sourceMatches || playModeNameMatches)
                {
                    matches.Add(candidate);
                }
            }

            return matches.OrderBy(candidate => candidate.GetInstanceID()).ToList();
        }

        private static ErrorResponse CreateLifecycleDestroyedError(
            string prefabPath,
            string phase,
            IReadOnlyList<Dictionary<string, string>> logs,
            bool assetLoaded)
        {
            bool isPrefab = !string.IsNullOrEmpty(prefabPath);
            string code = isPrefab ? "prefab_instance_destroyed" : "gameobject_destroyed_during_creation";
            string message = isPrefab
                ? $"Prefab asset '{prefabPath}' loaded successfully, but its scene instance was destroyed during Unity lifecycle processing."
                : "The newly created GameObject was destroyed during Unity lifecycle processing.";

            return ErrorResponse.Structured(
                code,
                message,
                new
                {
                    phase,
                    prefabPath,
                    editorMode = EditorApplication.isPlaying ? "play_mode" : "edit_mode",
                    assetLoaded,
                    instanceSurvived = false,
                    stateChanged = true,
                    retryable = false,
                    logs
                },
                "Inspect Awake, OnEnable, OnValidate, parent-change callbacks, and singleton or persistence policies on the created object.");
        }

        private static ErrorResponse ValidateCreatedObject(
            GameObject candidate,
            string prefabPath,
            string phase,
            IReadOnlyList<Dictionary<string, string>> logs,
            bool assetLoaded)
        {
            return candidate == null
                ? CreateLifecycleDestroyedError(prefabPath, phase, logs, assetLoaded)
                : null;
        }

        private static bool IndicatesLifecycleDestruction(
            Exception exception,
            IReadOnlyList<Dictionary<string, string>> logs)
        {
            if (exception?.Message?.IndexOf("destroy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return logs.Any(entry =>
                entry.TryGetValue("message", out string message) &&
                message.IndexOf("destroy", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static object Handle(JObject @params)
        {
            string name = @params["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                return new ErrorResponse("'name' parameter is required for 'create' action.");
            }

            // Get prefab creation parameters
            bool saveAsPrefab = @params["saveAsPrefab"]?.ToObject<bool>() ?? false;
            string prefabPath = @params["prefabPath"]?.ToString();
            string tag = @params["tag"]?.ToString();
            string primitiveType = @params["primitiveType"]?.ToString();
            GameObject newGo = null;
            IReadOnlyList<Dictionary<string, string>> creationDiagnostics = Array.Empty<Dictionary<string, string>>();

            if (!TryReadOptionalBoolean(@params, "allowPlayModeCreate", false, out bool allowPlayModeCreate))
            {
                return ErrorResponse.Structured(
                    "invalid_parameter",
                    "'allowPlayModeCreate' must be a boolean.",
                    new { parameter = "allowPlayModeCreate", stateChanged = false, retryable = false });
            }

            if (!TryReadOptionalBoolean(@params, "setActive", true, out bool setActive))
            {
                return ErrorResponse.Structured(
                    "invalid_parameter",
                    "'setActive' must be a boolean.",
                    new { parameter = "setActive", stateChanged = false, retryable = false });
            }

            bool hasSetActive = @params["setActive"] != null && @params["setActive"].Type != JTokenType.Null;
            string rawInstancePolicy = @params["instancePolicy"]?.ToString();
            if (!TryParseInstancePolicy(rawInstancePolicy, out PrefabInstancePolicy instancePolicy))
            {
                return ErrorResponse.Structured(
                    "invalid_instance_policy",
                    $"Unsupported instance policy '{rawInstancePolicy}'.",
                    new
                    {
                        provided = rawInstancePolicy,
                        allowed = new[] { "always_create", "fail_if_same_prefab", "reuse_same_prefab" },
                        stateChanged = false,
                        retryable = false
                    },
                    "Use always_create, fail_if_same_prefab, or reuse_same_prefab.");
            }

            // --- Try Instantiating Prefab First ---
            string originalPrefabPath = prefabPath;
            if (!saveAsPrefab && !string.IsNullOrEmpty(prefabPath))
            {
                string extension = System.IO.Path.GetExtension(prefabPath);

                if (!prefabPath.Contains("/") && (string.IsNullOrEmpty(extension) || extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase)))
                {
                    string prefabNameOnly = prefabPath;
                    McpLog.Info($"[ManageGameObject.Create] Searching for prefab named: '{prefabNameOnly}'");
                    string[] guids = AssetDatabase.FindAssets($"t:Prefab {prefabNameOnly}");
                    if (guids.Length == 0)
                    {
                        return new ErrorResponse($"Prefab named '{prefabNameOnly}' not found anywhere in the project.");
                    }
                    else if (guids.Length > 1)
                    {
                        string foundPaths = string.Join(", ", guids.Select(g => AssetDatabase.GUIDToAssetPath(g)));
                        return new ErrorResponse($"Multiple prefabs found matching name '{prefabNameOnly}': {foundPaths}. Please provide a more specific path.");
                    }
                    else
                    {
                        prefabPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                        McpLog.Info($"[ManageGameObject.Create] Found unique prefab at path: '{prefabPath}'");
                    }
                }
                else if (prefabPath.Contains("/") && string.IsNullOrEmpty(extension))
                {
                    McpLog.Warn($"[ManageGameObject.Create] Provided prefabPath '{prefabPath}' has no extension. Assuming it's a prefab and appending .prefab.");
                    prefabPath += ".prefab";
                }
                else if (!prefabPath.Contains("/") && !string.IsNullOrEmpty(extension) && !extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = prefabPath;
                    string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(fileName);
                    McpLog.Info($"[ManageGameObject.Create] Searching for asset file named: '{fileName}'");

                    string[] guids = AssetDatabase.FindAssets(fileNameWithoutExtension);
                    var matches = guids
                        .Select(g => AssetDatabase.GUIDToAssetPath(g))
                        .Where(p => p.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) || p.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (matches.Length == 0)
                    {
                        return new ErrorResponse($"Asset file '{fileName}' not found anywhere in the project.");
                    }
                    else if (matches.Length > 1)
                    {
                        string foundPaths = string.Join(", ", matches);
                        return new ErrorResponse($"Multiple assets found matching file name '{fileName}': {foundPaths}. Please provide a more specific path.");
                    }
                    else
                    {
                        prefabPath = matches[0];
                        McpLog.Info($"[ManageGameObject.Create] Found unique asset at path: '{prefabPath}'");
                    }
                }

                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabAsset != null)
                {
                    List<GameObject> existingInstances = instancePolicy == PrefabInstancePolicy.AlwaysCreate
                        ? new List<GameObject>()
                        : FindExistingPrefabInstances(prefabAsset, prefabPath);
                    if (existingInstances.Count > 0 && instancePolicy == PrefabInstancePolicy.FailIfSamePrefab)
                    {
                        return ErrorResponse.Structured(
                            "prefab_instance_exists",
                            $"At least one loaded scene instance already matches prefab '{prefabPath}'.",
                            new
                            {
                                phase = "duplicate_preflight",
                                prefabPath,
                                editorMode = EditorApplication.isPlaying ? "play_mode" : "edit_mode",
                                matchCount = existingInstances.Count,
                                existingInstances = existingInstances.Take(MaxCapturedLogs).Select(instance => new
                                {
                                    name = instance.name,
                                    instanceId = instance.GetInstanceID(),
                                    scene = instance.scene.name
                                }).ToArray(),
                                stateChanged = false,
                                retryable = false
                            },
                            "Target the existing instance or use reuse_same_prefab. Use always_create only when another instance is intentional.");
                    }

                    if (existingInstances.Count > 0 && instancePolicy == PrefabInstancePolicy.ReuseSamePrefab)
                    {
                        GameObject existingInstance = existingInstances[0];
                        Selection.activeGameObject = existingInstance;
                        return new SuccessResponse(
                            $"Reused existing scene instance '{existingInstance.name}' for prefab '{prefabPath}'.",
                            Helpers.GameObjectSerializer.GetGameObjectData(existingInstance));
                    }

                    if (IsPlayModePrefabCreationBlocked(EditorApplication.isPlaying, allowPlayModeCreate))
                    {
                        return ErrorResponse.Structured(
                            "play_mode_create_blocked",
                            "Prefab creation is blocked while Unity is in Play Mode unless allowPlayModeCreate (allow_play_mode_create in the MCP tool) is explicitly enabled.",
                            new
                            {
                                phase = "preflight",
                                prefabPath,
                                editorMode = "play_mode",
                                assetLoaded = true,
                                instanceSurvived = false,
                                stateChanged = false,
                                retryable = false
                            },
                            "Exit Play Mode, reuse an existing runtime instance, or retry with allowPlayModeCreate=true (allow_play_mode_create=true in the MCP tool) after confirming the lifecycle side effects are intentional.");
                    }

                    try
                    {
                        using (ScopedLogCollector logCollector = new())
                        {
                            try
                            {
                                newGo = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
                            }
                            finally
                            {
                                creationDiagnostics = logCollector.Snapshot();
                            }
                        }

                        if (newGo == null)
                        {
                            return CreateLifecycleDestroyedError(prefabPath, "prefab_instantiation", creationDiagnostics, true);
                        }
                        if (!string.IsNullOrEmpty(name))
                        {
                            newGo.name = name;
                        }
                        Undo.RegisterCreatedObjectUndo(newGo, $"Instantiate Prefab '{prefabAsset.name}' as '{newGo.name}'");
                        McpLog.Info($"[ManageGameObject.Create] Instantiated prefab '{prefabAsset.name}' from path '{prefabPath}' as '{newGo.name}'.");
                    }
                    catch (Exception e)
                    {
                        if (newGo == null && IndicatesLifecycleDestruction(e, creationDiagnostics))
                        {
                            return CreateLifecycleDestroyedError(prefabPath, "prefab_instantiation", creationDiagnostics, true);
                        }

                        string exceptionMessage = SecretRedactor.Scrub(e.Message);
                        return ErrorResponse.Structured(
                            "prefab_instantiation_exception",
                            $"Unity threw an exception while instantiating prefab '{prefabPath}': {exceptionMessage}",
                            new
                            {
                                phase = "prefab_instantiation",
                                prefabPath,
                                editorMode = EditorApplication.isPlaying ? "play_mode" : "edit_mode",
                                assetLoaded = true,
                                instanceSurvived = newGo != null,
                                stateChanged = newGo != null,
                                retryable = false,
                                exceptionType = e.GetType().FullName,
                                logs = creationDiagnostics
                            },
                            "Inspect the attached diagnostics and the prefab's lifecycle callbacks before retrying.");
                    }
                }
                else
                {
                    return new ErrorResponse($"Asset not found or not a GameObject at path: '{prefabPath}'.");
                }
            }

            // --- Fallback: Create Primitive or Empty GameObject ---
            bool createdNewObject = false;
            if (newGo == null)
            {
                if (!string.IsNullOrEmpty(primitiveType))
                {
                    try
                    {
                        PrimitiveType type = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), primitiveType, true);
                        newGo = GameObject.CreatePrimitive(type);
                        if (!string.IsNullOrEmpty(name))
                        {
                            newGo.name = name;
                        }
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(newGo);
                            return new ErrorResponse("'name' parameter is required when creating a primitive.");
                        }
                        createdNewObject = true;
                    }
                    catch (ArgumentException)
                    {
                        return new ErrorResponse($"Invalid primitive type: '{primitiveType}'. Valid types: {string.Join(", ", Enum.GetNames(typeof(PrimitiveType)))}");
                    }
                    catch (Exception e)
                    {
                        return new ErrorResponse($"Failed to create primitive '{primitiveType}': {e.Message}");
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return new ErrorResponse("'name' parameter is required for 'create' action when not instantiating a prefab or creating a primitive.");
                    }
                    newGo = new GameObject(name);
                    createdNewObject = true;
                }

                if (createdNewObject)
                {
                    Undo.RegisterCreatedObjectUndo(newGo, $"Create GameObject '{newGo.name}'");
                }
            }

            if (newGo == null)
            {
                return new ErrorResponse("Failed to create or instantiate the GameObject.");
            }

            Undo.RecordObject(newGo.transform, "Set GameObject Transform");
            Undo.RecordObject(newGo, "Set GameObject Properties");

            // Set Parent
            JToken parentToken = @params["parent"];
            if (parentToken != null)
            {
                GameObject parentGo = ManageGameObjectCommon.FindObjectInternal(parentToken, "by_id_or_name_or_path");
                if (parentGo == null)
                {
                    UnityEngine.Object.DestroyImmediate(newGo);
                    return new ErrorResponse($"Parent specified ('{parentToken}') but not found.");
                }
                newGo.transform.SetParent(parentGo.transform, true);
                ErrorResponse parentLifecycleFailure = ValidateCreatedObject(
                    newGo,
                    prefabPath,
                    "parent_assignment",
                    creationDiagnostics,
                    !string.IsNullOrEmpty(prefabPath));
                if (parentLifecycleFailure != null)
                {
                    return parentLifecycleFailure;
                }
            }

            // Set Transform
            Vector3? position = VectorParsing.ParseVector3(@params["position"]);
            Vector3? rotation = VectorParsing.ParseVector3(@params["rotation"]);
            Vector3? scale = VectorParsing.ParseVector3(@params["scale"]);

            if (position.HasValue) newGo.transform.localPosition = position.Value;
            if (rotation.HasValue) newGo.transform.localEulerAngles = rotation.Value;
            if (scale.HasValue) newGo.transform.localScale = scale.Value;

            // Set Tag
            if (!string.IsNullOrEmpty(tag))
            {
                if (tag != "Untagged" && !System.Linq.Enumerable.Contains(InternalEditorUtility.tags, tag))
                {
                    McpLog.Info($"[ManageGameObject.Create] Tag '{tag}' not found. Creating it.");
                    try
                    {
                        InternalEditorUtility.AddTag(tag);
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Object.DestroyImmediate(newGo);
                        return new ErrorResponse($"Failed to create tag '{tag}': {ex.Message}.");
                    }
                }

                try
                {
                    newGo.tag = tag;
                }
                catch (Exception ex)
                {
                    UnityEngine.Object.DestroyImmediate(newGo);
                    return new ErrorResponse($"Failed to set tag to '{tag}' during creation: {ex.Message}.");
                }
            }

            // Set Layer
            string layerName = @params["layer"]?.ToString();
            if (!string.IsNullOrEmpty(layerName))
            {
                int layerId = LayerMask.NameToLayer(layerName);
                if (layerId != -1)
                {
                    newGo.layer = layerId;
                }
                else
                {
                    McpLog.Warn($"[ManageGameObject.Create] Layer '{layerName}' not found. Using default layer.");
                }
            }

            // Add Components
            if (@params["componentsToAdd"] is JArray componentsToAddArray)
            {
                foreach (var compToken in componentsToAddArray)
                {
                    string typeName = null;
                    JObject properties = null;

                    if (compToken.Type == JTokenType.String)
                    {
                        typeName = compToken.ToString();
                    }
                    else if (compToken is JObject compObj)
                    {
                        typeName = compObj["typeName"]?.ToString();
                        properties = compObj["properties"] as JObject;
                    }

                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var addResult = GameObjectComponentHelpers.AddComponentInternal(newGo, typeName, properties);
                        ErrorResponse componentLifecycleFailure = ValidateCreatedObject(
                            newGo,
                            prefabPath,
                            $"component_add:{typeName}",
                            creationDiagnostics,
                            !string.IsNullOrEmpty(prefabPath));
                        if (componentLifecycleFailure != null)
                        {
                            return componentLifecycleFailure;
                        }
                        if (addResult != null)
                        {
                            UnityEngine.Object.DestroyImmediate(newGo);
                            return addResult;
                        }
                    }
                    else
                    {
                        McpLog.Warn($"[ManageGameObject] Invalid component format in componentsToAdd: {compToken}");
                    }
                }
            }

            if (hasSetActive)
            {
                newGo.SetActive(setActive);
                ErrorResponse activationLifecycleFailure = ValidateCreatedObject(
                    newGo,
                    prefabPath,
                    "activation",
                    creationDiagnostics,
                    !string.IsNullOrEmpty(prefabPath));
                if (activationLifecycleFailure != null)
                {
                    return activationLifecycleFailure;
                }
            }

            // Save as Prefab ONLY if we *created* a new object AND saveAsPrefab is true
            GameObject finalInstance = newGo;
            if (createdNewObject && saveAsPrefab)
            {
                string finalPrefabPath = prefabPath;
                if (string.IsNullOrEmpty(finalPrefabPath))
                {
                    UnityEngine.Object.DestroyImmediate(newGo);
                    return new ErrorResponse("'prefabPath' is required when 'saveAsPrefab' is true and creating a new object.");
                }
                if (!finalPrefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    McpLog.Info($"[ManageGameObject.Create] Appending .prefab extension to save path: '{finalPrefabPath}' -> '{finalPrefabPath}.prefab'");
                    finalPrefabPath += ".prefab";
                }

                try
                {
                    string directoryPath = System.IO.Path.GetDirectoryName(finalPrefabPath);
                    if (!string.IsNullOrEmpty(directoryPath) && !System.IO.Directory.Exists(directoryPath))
                    {
                        System.IO.Directory.CreateDirectory(directoryPath);
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                        McpLog.Info($"[ManageGameObject.Create] Created directory for prefab: {directoryPath}");
                    }

                    finalInstance = PrefabUtility.SaveAsPrefabAssetAndConnect(newGo, finalPrefabPath, InteractionMode.UserAction);

                    if (finalInstance == null)
                    {
                        UnityEngine.Object.DestroyImmediate(newGo);
                        return new ErrorResponse($"Failed to save GameObject '{name}' as prefab at '{finalPrefabPath}'. Check path and permissions.");
                    }
                    ErrorResponse saveLifecycleFailure = ValidateCreatedObject(
                        finalInstance,
                        finalPrefabPath,
                        "prefab_save_and_connect",
                        creationDiagnostics,
                        true);
                    if (saveLifecycleFailure != null)
                    {
                        return saveLifecycleFailure;
                    }
                    McpLog.Info($"[ManageGameObject.Create] GameObject '{name}' saved as prefab to '{finalPrefabPath}' and instance connected.");
                }
                catch (Exception e)
                {
                    UnityEngine.Object.DestroyImmediate(newGo);
                    return new ErrorResponse($"Error saving prefab '{finalPrefabPath}': {e.Message}");
                }
            }

            ErrorResponse finalLifecycleFailure = ValidateCreatedObject(
                finalInstance,
                prefabPath,
                "final_serialization",
                creationDiagnostics,
                !string.IsNullOrEmpty(prefabPath));
            if (finalLifecycleFailure != null)
            {
                return finalLifecycleFailure;
            }

            Selection.activeGameObject = finalInstance;

            string messagePrefabPath =
                finalInstance == null
                    ? originalPrefabPath
                    : AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(finalInstance) ?? (UnityEngine.Object)finalInstance);

            string successMessage;
            if (!createdNewObject && !string.IsNullOrEmpty(messagePrefabPath))
            {
                successMessage = $"Prefab '{messagePrefabPath}' instantiated successfully as '{finalInstance.name}'.";
            }
            else if (createdNewObject && saveAsPrefab && !string.IsNullOrEmpty(messagePrefabPath))
            {
                successMessage = $"GameObject '{finalInstance.name}' created and saved as prefab to '{messagePrefabPath}'.";
            }
            else
            {
                successMessage = $"GameObject '{finalInstance.name}' created successfully in scene.";
            }

            return new SuccessResponse(
                successMessage,
                Helpers.GameObjectSerializer.GetGameObjectData(finalInstance),
                creationDiagnostics.Count > 0 ? creationDiagnostics : null);
        }
    }
}
