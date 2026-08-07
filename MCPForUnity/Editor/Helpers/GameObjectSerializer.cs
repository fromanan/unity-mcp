using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Runtime.Serialization; // For Converters
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Runtime.Helpers;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Handles serialization of GameObjects and Components for MCP responses.
    /// Includes reflection helpers and caching for performance.
    /// </summary> 
    public static class GameObjectSerializer
    {
        // --- Data Serialization ---

        /// <summary>
        /// Creates a serializable representation of a GameObject.
        /// </summary>
        public static object GetGameObjectData(GameObject go)
        {
            if (go == null)
                return null;
            return new
            {
                name = go.name,
                instanceID = go.GetInstanceIDCompat(),
                tag = go.tag,
                layer = go.layer,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                isStatic = go.isStatic,
                scenePath = go.scene.path, // Identify which scene it belongs to
                transform = new // Serialize transform components carefully to avoid JSON issues
                {
                    // Serialize Vector3 components individually to prevent self-referencing loops.
                    // The default serializer can struggle with properties like Vector3.normalized.
                    position = new
                    {
                        x = go.transform.position.x,
                        y = go.transform.position.y,
                        z = go.transform.position.z,
                    },
                    localPosition = new
                    {
                        x = go.transform.localPosition.x,
                        y = go.transform.localPosition.y,
                        z = go.transform.localPosition.z,
                    },
                    rotation = new
                    {
                        x = go.transform.rotation.eulerAngles.x,
                        y = go.transform.rotation.eulerAngles.y,
                        z = go.transform.rotation.eulerAngles.z,
                    },
                    localRotation = new
                    {
                        x = go.transform.localRotation.eulerAngles.x,
                        y = go.transform.localRotation.eulerAngles.y,
                        z = go.transform.localRotation.eulerAngles.z,
                    },
                    scale = new
                    {
                        x = go.transform.localScale.x,
                        y = go.transform.localScale.y,
                        z = go.transform.localScale.z,
                    },
                    forward = new
                    {
                        x = go.transform.forward.x,
                        y = go.transform.forward.y,
                        z = go.transform.forward.z,
                    },
                    up = new
                    {
                        x = go.transform.up.x,
                        y = go.transform.up.y,
                        z = go.transform.up.z,
                    },
                    right = new
                    {
                        x = go.transform.right.x,
                        y = go.transform.right.y,
                        z = go.transform.right.z,
                    },
                },
                parentInstanceID = go.transform.parent?.gameObject.GetInstanceIDCompat() ?? 0, // 0 if no parent
                // Optionally include components, but can be large
                // components = go.GetComponents<Component>().Select(c => GetComponentData(c)).ToList()
                // Or just component names:
                componentNames = go.GetComponents<Component>()
                    .Select(c => c.GetType().FullName)
                    .ToList(),
            };
        }

        // --- Metadata Caching for Reflection ---
        private class CachedMetadata
        {
            public readonly List<PropertyInfo> SerializableProperties;
            public readonly List<FieldInfo> SerializableFields;
            public readonly List<string> OmittedPropertyNames;

            public CachedMetadata(List<PropertyInfo> properties, List<FieldInfo> fields, List<string> omittedPropertyNames)
            {
                SerializableProperties = properties;
                SerializableFields = fields;
                OmittedPropertyNames = omittedPropertyNames;
            }
        }
        // Key becomes Tuple<Type, bool>
        private static readonly Dictionary<Tuple<Type, bool>, CachedMetadata> _metadataCache = new Dictionary<Tuple<Type, bool>, CachedMetadata>();
        private const int MaxComponentValueNodes = 4096;
        private const int MaxCollectionItems = 128;
        private const int MaxObjectFields = 64;
        private const int MaxSerializationDepth = 6;
        private const int MaxStringCharacters = 8192;

        private sealed class SerializationBudget
        {
            public int RemainingNodes { get; private set; }
            public bool Truncated { get; private set; }

            public SerializationBudget(int maxNodes)
            {
                RemainingNodes = maxNodes;
            }

            public bool TryConsume()
            {
                if (RemainingNodes <= 0)
                {
                    Truncated = true;
                    return false;
                }

                RemainingNodes--;
                return true;
            }

            public void MarkTruncated()
            {
                Truncated = true;
            }
        }
        // --- End Metadata Caching ---

        /// <summary>
        /// Checks if a type is or derives from a type with the specified full name.
        /// Used to detect special-case components including their subclasses.
        /// </summary>
        private static bool IsOrDerivedFrom(Type type, string baseTypeFullName)
        {
            Type current = type;
            while (current != null)
            {
                if (current.FullName == baseTypeFullName)
                    return true;
                current = current.BaseType;
            }
            return false;
        }

        // Type full names that are known to crash the Editor when accessed via reflection.
        // Photon Fusion uses IL weaving to inject fields with these types into NetworkBehaviour
        // subclasses. They contain native/unmanaged memory and cannot be safely serialized.
        private static readonly HashSet<string> _crashingTypeNames = new HashSet<string>
        {
            "Fusion.NetworkBehaviourBuffer",
            "Fusion.NetworkBehaviourCallbackBuffer",
            "Fusion.Networked+Internals",
            "Fusion.Changed`1",
        };
        private static readonly PropertyInfo _isByRefLikeProperty = typeof(Type).GetProperty("IsByRefLike");

        /// <summary>
        /// Checks if a type is unsafe to access via reflection or serialize.
        /// Returns true for ref structs (Span, ReadOnlySpan), pointer types,
        /// by-ref types, and known IL-weaved types that crash the Editor.
        /// </summary>
        private static bool IsUnsafeType(Type type)
        {
            return IsUnsafeType(type, new HashSet<Type>());
        }

        private static bool IsUnsafeType(Type type, HashSet<Type> visitedTypes)
        {
            if (type == null) return false;
            if (!visitedTypes.Add(type)) return false;

            // Pointer and by-ref types cannot be serialized
            if (type.IsPointer || type.IsByRef)
                return true;

            // Ref structs (Span<>, ReadOnlySpan<>, etc.) cannot be boxed. Use reflection
            // so Unity versions without Type.IsByRefLike still compile.
            if (type.IsValueType && _isByRefLikeProperty != null && (bool)_isByRefLikeProperty.GetValue(type, null))
                return true;

            // Check the type and its generic definition against the blacklist
            string fullName = type.FullName;
            if (fullName != null && _crashingTypeNames.Contains(fullName))
                return true;

            // Unity 6.3 TransformHandles are native handles whose enumerable properties throw
            // when the handle is not initialized or its backing object has been destroyed.
            if (fullName == "UnityEngine.TransformHandle")
                return true;

            if (type.IsGenericType)
            {
                string genericFullName = type.GetGenericTypeDefinition()?.FullName;
                if (genericFullName != null && _crashingTypeNames.Contains(genericFullName))
                    return true;
            }

            // Catch-all for Fusion buffer types injected by IL weaving
            if (fullName != null && fullName.StartsWith("Fusion.") && fullName.Contains("Buffer"))
                return true;

            // Arrays and generic containers can wrap unsafe Fusion/ref-like types.
            // Newtonsoft.Json would still recurse into those values during serialization.
            Type elementType = type.GetElementType();
            if (elementType != null && IsUnsafeType(elementType, visitedTypes))
                return true;

            foreach (Type genericArgument in type.GetGenericArguments())
            {
                if (IsUnsafeType(genericArgument, visitedTypes))
                    return true;
            }

            return false;
        }

        private static bool IsPotentiallyUnboundedProperty(Type componentType, PropertyInfo property)
        {
            if (property == null || property.PropertyType == typeof(string)) return false;

            string propertyName = property.Name;
            if (typeof(Renderer).IsAssignableFrom(componentType)
                && (propertyName == "material" || propertyName == "materials"))
            {
                return false;
            }

            if (typeof(MeshFilter).IsAssignableFrom(componentType) && propertyName == "mesh")
            {
                return false;
            }

            Type propertyType = property.PropertyType;
            return propertyType.IsArray || typeof(IEnumerable).IsAssignableFrom(propertyType);
        }

        private static bool IsUnavailableNavMeshAgentProperty(Component component, string propertyName)
        {
            if (!(component is UnityEngine.AI.NavMeshAgent navMeshAgent))
                return false;

            bool requiresActiveAgent = propertyName == nameof(UnityEngine.AI.NavMeshAgent.remainingDistance)
                || propertyName == nameof(UnityEngine.AI.NavMeshAgent.isStopped);
            if (!requiresActiveAgent)
                return false;

            return !navMeshAgent.isActiveAndEnabled || !navMeshAgent.isOnNavMesh;
        }

        /// <summary>
        /// Serializes a UnityEngine.Object reference to a dictionary with name, instanceID, and assetPath.
        /// Used for consistent serialization of asset references in special-case component handlers.
        /// </summary>
        /// <param name="obj">The Unity object to serialize</param>
        /// <param name="includeAssetPath">Whether to include the asset path (default true)</param>
        /// <returns>A dictionary with the object's reference info, or null if obj is null</returns>
        private static Dictionary<string, object> SerializeAssetReference(UnityEngine.Object obj, bool includeAssetPath = true)
        {
            if (obj == null) return null;
            
            var result = new Dictionary<string, object>
            {
                { "name", obj.name },
                { "instanceID", obj.GetInstanceIDCompat() }
            };
            
            if (includeAssetPath)
            {
                var assetPath = AssetDatabase.GetAssetPath(obj);
                result["assetPath"] = string.IsNullOrEmpty(assetPath) ? null : assetPath;
            }
            
            return result;
        }

        /// <summary>
        /// Creates a serializable representation of a Component, attempting to serialize
        /// public properties and fields using reflection, with caching and control over non-public fields.
        /// </summary>
        // Add the flag parameter here
        public static object GetComponentData(Component c, bool includeNonPublicSerializedFields = true)
        {
            // --- Add Early Logging --- 
            // McpLog.Info($"[GetComponentData] Starting for component: {c?.GetType()?.FullName ?? "null"} (ID: {c?.GetInstanceIDCompat() ?? 0})");
            // --- End Early Logging ---

            if (c == null) return null;
            Type componentType = c.GetType();
            var serializationBudget = new SerializationBudget(MaxComponentValueNodes);

            // --- Special handling for Transform to avoid reflection crashes and problematic properties --- 
            if (componentType == typeof(Transform))
            {
                Transform tr = c as Transform;
                // McpLog.Info($"[GetComponentData] Manually serializing Transform (ID: {tr.GetInstanceIDCompat()})");
                return new Dictionary<string, object>
                {
                    { "typeName", componentType.FullName },
                    { "instanceID", tr.GetInstanceIDCompat() },
                    // Manually extract known-safe properties. Avoid Quaternion 'rotation' and 'lossyScale'.
                    { "position", CreateTokenFromValue(tr.position, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "localPosition", CreateTokenFromValue(tr.localPosition, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "eulerAngles", CreateTokenFromValue(tr.eulerAngles, typeof(Vector3))?.ToObject<object>() ?? new JObject() }, // Use Euler angles
                    { "localEulerAngles", CreateTokenFromValue(tr.localEulerAngles, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "localScale", CreateTokenFromValue(tr.localScale, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "right", CreateTokenFromValue(tr.right, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "up", CreateTokenFromValue(tr.up, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "forward", CreateTokenFromValue(tr.forward, typeof(Vector3))?.ToObject<object>() ?? new JObject() },
                    { "parentInstanceID", tr.parent?.gameObject.GetInstanceIDCompat() ?? 0 },
                    { "rootInstanceID", tr.root?.gameObject.GetInstanceIDCompat() ?? 0 },
                    { "childCount", tr.childCount },
                    // Include standard Object/Component properties
                    { "name", tr.name },
                    { "tag", tr.tag },
                    { "gameObjectInstanceID", tr.gameObject?.GetInstanceIDCompat() ?? 0 }
                };
            }
            // --- End Special handling for Transform --- 

            // --- Special handling for Camera to avoid matrix-related crashes ---
            if (componentType == typeof(Camera))
            {
                Camera cam = c as Camera;
                var cameraProperties = new Dictionary<string, object>();

                // List of safe properties to serialize
                var safeProperties = new Dictionary<string, Func<object>>
                {
                    { "nearClipPlane", () => cam.nearClipPlane },
                    { "farClipPlane", () => cam.farClipPlane },
                    { "fieldOfView", () => cam.fieldOfView },
                    { "renderingPath", () => (int)cam.renderingPath },
                    { "actualRenderingPath", () => (int)cam.actualRenderingPath },
                    { "allowHDR", () => cam.allowHDR },
                    { "allowMSAA", () => cam.allowMSAA },
                    { "allowDynamicResolution", () => cam.allowDynamicResolution },
                    { "forceIntoRenderTexture", () => cam.forceIntoRenderTexture },
                    { "orthographicSize", () => cam.orthographicSize },
                    { "orthographic", () => cam.orthographic },
                    { "opaqueSortMode", () => (int)cam.opaqueSortMode },
                    { "transparencySortMode", () => (int)cam.transparencySortMode },
                    { "depth", () => cam.depth },
                    { "aspect", () => cam.aspect },
                    { "cullingMask", () => cam.cullingMask },
                    { "eventMask", () => cam.eventMask },
                    { "backgroundColor", () => cam.backgroundColor },
                    { "clearFlags", () => (int)cam.clearFlags },
                    { "stereoEnabled", () => cam.stereoEnabled },
                    { "stereoSeparation", () => cam.stereoSeparation },
                    { "stereoConvergence", () => cam.stereoConvergence },
                    { "enabled", () => cam.enabled },
                    { "name", () => cam.name },
                    { "tag", () => cam.tag },
                    { "gameObject", () => new { name = cam.gameObject.name, instanceID = cam.gameObject.GetInstanceIDCompat() } }
                };

                foreach (var prop in safeProperties)
                {
                    try
                    {
                        var value = prop.Value();
                        if (value != null)
                        {
                            AddSerializableValue(cameraProperties, prop.Key, value.GetType(), value, serializationBudget);
                        }
                    }
                    catch (Exception)
                    {
                        // Silently skip any property that fails
                        continue;
                    }
                }

                return new Dictionary<string, object>
                {
                    { "typeName", componentType.FullName },
                    { "instanceID", cam.GetInstanceIDCompat() },
                    { "properties", cameraProperties }
                };
            }
            // --- End Special handling for Camera ---

            // --- Special handling for UIDocument to avoid infinite loops from VisualElement hierarchy (Issue #585) ---
            // UIDocument.rootVisualElement contains circular parent/child references that cause infinite serialization loops.
            // Use IsOrDerivedFrom to also catch subclasses of UIDocument.
            if (IsOrDerivedFrom(componentType, "UnityEngine.UIElements.UIDocument"))
            {
                var uiDocProperties = new Dictionary<string, object>();

                try
                {
                    // Get panelSettings reference safely
                    var panelSettingsProp = componentType.GetProperty("panelSettings");
                    if (panelSettingsProp != null)
                    {
                        var panelSettings = panelSettingsProp.GetValue(c) as UnityEngine.Object;
                        uiDocProperties["panelSettings"] = SerializeAssetReference(panelSettings);
                    }

                    // Get visualTreeAsset reference safely (the UXML file)
                    var visualTreeAssetProp = componentType.GetProperty("visualTreeAsset");
                    if (visualTreeAssetProp != null)
                    {
                        var visualTreeAsset = visualTreeAssetProp.GetValue(c) as UnityEngine.Object;
                        uiDocProperties["visualTreeAsset"] = SerializeAssetReference(visualTreeAsset);
                    }

                    // Get sortingOrder safely
                    var sortingOrderProp = componentType.GetProperty("sortingOrder");
                    if (sortingOrderProp != null)
                    {
                        uiDocProperties["sortingOrder"] = sortingOrderProp.GetValue(c);
                    }

                    // Get enabled state (from Behaviour base class)
                    var enabledProp = componentType.GetProperty("enabled");
                    if (enabledProp != null)
                    {
                        uiDocProperties["enabled"] = enabledProp.GetValue(c);
                    }

                    // Get parentUI reference safely (no asset path needed - it's a scene reference)
                    var parentUIProp = componentType.GetProperty("parentUI");
                    if (parentUIProp != null)
                    {
                        var parentUI = parentUIProp.GetValue(c) as UnityEngine.Object;
                        uiDocProperties["parentUI"] = SerializeAssetReference(parentUI, includeAssetPath: false);
                    }

                    // NOTE: rootVisualElement is intentionally skipped - it contains circular
                    // parent/child references that cause infinite serialization loops
                    uiDocProperties["_note"] = "rootVisualElement skipped to prevent circular reference loops";
                }
                catch (Exception e)
                {
                    McpLog.Warn($"[GetComponentData] Error reading UIDocument properties: {e.Message}");
                }

                // Return structure matches Camera special handling (typeName, instanceID, properties)
                return new Dictionary<string, object>
                {
                    { "typeName", componentType.FullName },
                    { "instanceID", c.GetInstanceIDCompat() },
                    { "properties", uiDocProperties }
                };
            }
            // --- End Special handling for UIDocument ---

            var data = new Dictionary<string, object>
            {
                { "typeName", componentType.FullName },
                { "instanceID", c.GetInstanceIDCompat() }
            };

            // --- Get Cached or Generate Metadata (using new cache key) ---
            Tuple<Type, bool> cacheKey = new Tuple<Type, bool>(componentType, includeNonPublicSerializedFields);
            if (!_metadataCache.TryGetValue(cacheKey, out CachedMetadata cachedData))
            {
                var propertiesToCache = new List<PropertyInfo>();
                var fieldsToCache = new List<FieldInfo>();
                var omittedPropertyNames = new List<string>();

                // Traverse the hierarchy from the component type up to MonoBehaviour
                Type currentType = componentType;
                while (currentType != null && currentType != typeof(MonoBehaviour) && currentType != typeof(object))
                {
                    // Get properties declared only at the current type level
                    BindingFlags propFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    foreach (var propInfo in currentType.GetProperties(propFlags))
                    {
                        // Basic filtering (readable, not indexer, not transform which is handled elsewhere)
                        if (!propInfo.CanRead || propInfo.GetIndexParameters().Length > 0 || propInfo.Name == "transform") continue;
                        // Skip properties whose return type would crash when accessed via reflection
                        // (e.g. Fusion IL-weaved types, Span<>, ReadOnlySpan<>, pointers)
                        if (IsUnsafeType(propInfo.PropertyType) || IsPotentiallyUnboundedProperty(componentType, propInfo))
                        {
                            if (omittedPropertyNames.Count < MaxObjectFields)
                                omittedPropertyNames.Add(propInfo.Name);
                            continue;
                        }
                        // Add if not already added (handles overrides - keep the most derived version)
                        if (!propertiesToCache.Any(p => p.Name == propInfo.Name))
                        {
                            propertiesToCache.Add(propInfo);
                        }
                    }

                    // Get fields declared only at the current type level (both public and non-public)
                    BindingFlags fieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    var declaredFields = currentType.GetFields(fieldFlags);

                    // Process the declared Fields for caching
                    foreach (var fieldInfo in declaredFields)
                    {
                        if (fieldInfo.Name.EndsWith("k__BackingField")) continue; // Skip backing fields
                        // Skip fields whose type would crash when accessed via reflection
                        // (e.g. Fusion IL-weaved types, Span<>, ReadOnlySpan<>, pointers)
                        if (IsUnsafeType(fieldInfo.FieldType)) continue;

                        // Add if not already added (handles hiding - keep the most derived version)
                        if (fieldsToCache.Any(f => f.Name == fieldInfo.Name)) continue;

                        bool shouldInclude = false;
                        if (includeNonPublicSerializedFields)
                        {
                            // If TRUE, include Public OR any NonPublic with [SerializeField] (private/protected/internal)
                            var hasSerializeField = fieldInfo.IsDefined(typeof(SerializeField), inherit: true);
                            shouldInclude = fieldInfo.IsPublic || (!fieldInfo.IsPublic && hasSerializeField);
                        }
                        else // includeNonPublicSerializedFields is FALSE
                        {
                            // If FALSE, include ONLY if it is explicitly Public.
                            shouldInclude = fieldInfo.IsPublic;
                        }

                        if (shouldInclude)
                        {
                            fieldsToCache.Add(fieldInfo);
                        }
                    }

                    // Move to the base type
                    currentType = currentType.BaseType;
                }
                // --- End Hierarchy Traversal ---

                cachedData = new CachedMetadata(propertiesToCache, fieldsToCache, omittedPropertyNames);
                _metadataCache[cacheKey] = cachedData; // Add to cache with combined key
            }
            // --- End Get Cached or Generate Metadata ---

            // --- Use cached metadata ---
            var serializablePropertiesOutput = new Dictionary<string, object>();

            // --- Add Logging Before Property Loop ---
            // McpLog.Info($"[GetComponentData] Starting property loop for {componentType.Name}...");
            // --- End Logging Before Property Loop ---

            // Use cached properties
            foreach (var propInfo in cachedData.SerializableProperties)
            {
                string propName = propInfo.Name;

                // --- Skip known obsolete/problematic Component shortcut properties ---
                bool skipProperty = false;
                if (propName == "rigidbody" || propName == "rigidbody2D" || propName == "camera" ||
                    propName == "light" || propName == "animation" || propName == "constantForce" ||
                    propName == "renderer" || propName == "audio" || propName == "networkView" ||
                    propName == "collider" || propName == "collider2D" || propName == "hingeJoint" ||
                    propName == "particleSystem" ||
                    // Also skip potentially problematic Matrix properties prone to cycles/errors
                    propName == "worldToLocalMatrix" || propName == "localToWorldMatrix")
                {
                    // McpLog.Info($"[GetComponentData] Explicitly skipping generic property: {propName}"); // Optional log
                    skipProperty = true;
                }
                // --- End Skip Generic Properties ---

                // --- Skip specific potentially problematic Camera properties ---
                if (componentType == typeof(Camera) &&
                    (propName == "pixelRect" ||
                     propName == "rect" ||
                     propName == "cullingMatrix" ||
                     propName == "useOcclusionCulling" ||
                     propName == "worldToCameraMatrix" ||
                     propName == "projectionMatrix" ||
                     propName == "nonJitteredProjectionMatrix" ||
                     propName == "previousViewProjectionMatrix" ||
                     propName == "cameraToWorldMatrix"))
                {
                    // McpLog.Info($"[GetComponentData] Explicitly skipping Camera property: {propName}");
                    skipProperty = true;
                }
                // --- End Skip Camera Properties ---

                // --- Skip specific potentially problematic Transform properties ---
                if (componentType == typeof(Transform) &&
                    (propName == "lossyScale" ||
                     propName == "rotation" ||
                     propName == "worldToLocalMatrix" ||
                     propName == "localToWorldMatrix"))
                {
                    skipProperty = true;
                }
                // --- End Skip Transform Properties ---

                // --- Skip Collider properties that cause native crashes via PhysX ---
                if (typeof(Collider).IsAssignableFrom(componentType) &&
                    propName == "GeometryHolder")
                {
                    skipProperty = true;
                }
                // --- End Skip Collider Properties ---

                if (IsUnavailableNavMeshAgentProperty(c, propName))
                {
                    skipProperty = true;
                }

                // Skip if flagged
                if (skipProperty)
                {
                    continue;
                }

                try
                {
                    // --- Add detailed logging --- 
                    // McpLog.Info($"[GetComponentData] Accessing: {componentType.Name}.{propName}");
                    // --- End detailed logging ---

                    // --- Special handling for material/mesh properties ---
                    object value;
                    if (propName == "material" || propName == "materials" || propName == "mesh")
                    {
                        // Inspection must never instantiate per-renderer materials or duplicate a mesh.
                        if ((propName == "material" || propName == "materials") && c is Renderer renderer)
                        {
                            if (propName == "material")
                                value = renderer.sharedMaterial;
                            else // materials
                                value = renderer.sharedMaterials;
                        }
                        else if (propName == "mesh" && c is MeshFilter meshFilter)
                        {
                            value = meshFilter.sharedMesh;
                        }
                        else
                        {
                            // Fallback to normal property access if type doesn't match
                            value = propInfo.GetValue(c);
                        }
                    }
                    else
                    {
                        value = propInfo.GetValue(c);
                    }
                    // --- End special handling ---

                    Type propType = propInfo.PropertyType;
                    AddSerializableValue(serializablePropertiesOutput, propName, propType, value, serializationBudget);
                }
                catch (Exception)
                {
                    // McpLog.Warn($"Could not read property {propName} on {componentType.Name}");
                }
            }

            // --- Add Logging Before Field Loop ---
            // McpLog.Info($"[GetComponentData] Starting field loop for {componentType.Name}...");
            // --- End Logging Before Field Loop ---

            // Use cached fields
            foreach (var fieldInfo in cachedData.SerializableFields)
            {
                try
                {
                    // --- Add detailed logging for fields --- 
                    // McpLog.Info($"[GetComponentData] Accessing Field: {componentType.Name}.{fieldInfo.Name}");
                    // --- End detailed logging for fields ---
                    object value = fieldInfo.GetValue(c);
                    string fieldName = fieldInfo.Name;
                    Type fieldType = fieldInfo.FieldType;
                    AddSerializableValue(serializablePropertiesOutput, fieldName, fieldType, value, serializationBudget);
                }
                catch (Exception)
                {
                    // McpLog.Warn($"Could not read field {fieldInfo.Name} on {componentType.Name}");
                }
            }
            // --- End Use cached metadata ---

            if (serializablePropertiesOutput.Count > 0)
            {
                data["properties"] = serializablePropertiesOutput;
            }

            if (serializationBudget.Truncated || cachedData.OmittedPropertyNames.Count > 0)
            {
                data["serialization"] = new Dictionary<string, object>
                {
                    { "truncated", serializationBudget.Truncated },
                    { "maxNodes", MaxComponentValueNodes },
                    { "maxCollectionItems", MaxCollectionItems },
                    { "maxDepth", MaxSerializationDepth },
                    { "omittedProperties", cachedData.OmittedPropertyNames }
                };
            }

            return data;
        }

        // Helper function to decide how to serialize different types
        private static void AddSerializableValue(
            Dictionary<string, object> dict,
            string name,
            Type type,
            object value,
            SerializationBudget budget)
        {
            // Simplified: Directly use CreateTokenFromValue which uses the serializer
            if (value == null)
            {
                dict[name] = null;
                return;
            }

            try
            {
                dict[name] = ConvertValueToBoundedObject(value, type, budget, 0);
            }
            catch (Exception e)
            {
                // Catch potential errors during JToken conversion or addition to dictionary
                McpLog.Warn($"[AddSerializableValue] Error processing value for '{name}' (Type: {type.FullName}): {e.Message}. Skipping.");
            }
        }

        public static object SerializeValue(object value)
        {
            if (value == null) return null;
            if (value is GameObject gameObject) return GetGameObjectData(gameObject);
            if (value is Component component) return GetComponentData(component, includeNonPublicSerializedFields: false);

            var budget = new SerializationBudget(MaxComponentValueNodes);
            object serialized = ConvertValueToBoundedObject(value, value.GetType(), budget, 0);
            if (!budget.Truncated) return serialized;

            return new Dictionary<string, object>
            {
                { "value", serialized },
                { "serialization", new Dictionary<string, object>
                    {
                        { "truncated", true },
                        { "maxNodes", MaxComponentValueNodes },
                        { "maxCollectionItems", MaxCollectionItems },
                        { "maxDepth", MaxSerializationDepth }
                    }
                }
            };
        }

        private static object ConvertValueToBoundedObject(
            object value,
            Type declaredType,
            SerializationBudget budget,
            int depth)
        {
            if (value == null) return null;
            if (!budget.TryConsume()) return TruncationMarker("node_budget");
            if (depth > MaxSerializationDepth)
            {
                budget.MarkTruncated();
                return TruncationMarker("max_depth");
            }

            Type runtimeType = value.GetType();
            if (IsUnsafeType(declaredType) || IsUnsafeType(runtimeType))
            {
                budget.MarkTruncated();
                return TruncationMarker("unsafe_type", runtimeType.FullName);
            }

            if (value is string text)
            {
                if (text.Length <= MaxStringCharacters) return text;
                budget.MarkTruncated();
                return text.Substring(0, MaxStringCharacters) + "...[truncated]";
            }

            if (runtimeType.IsPrimitive || runtimeType.IsEnum || value is decimal)
                return value;
            if (value is DateTime || value is DateTimeOffset || value is Guid || value is TimeSpan || value is Uri)
                return value.ToString();
            if (value is UnityEngine.Object unityObject)
                return SerializeAssetReference(unityObject);

            if (value is Vector2 || value is Vector3 || value is Vector4 || value is Quaternion
                || value is Color || value is Rect || value is Bounds || value is Matrix4x4)
            {
                JToken token = CreateTokenFromValue(value, runtimeType);
                return ConvertJTokenToBoundedObject(token, budget, depth + 1);
            }

            if (value is JToken jToken)
                return ConvertJTokenToBoundedObject(jToken, budget, depth + 1);

            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object>();
                int count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count++ >= MaxCollectionItems || budget.RemainingNodes <= 0)
                    {
                        budget.MarkTruncated();
                        result["_truncated"] = true;
                        break;
                    }

                    string key = entry.Key?.ToString() ?? "null";
                    result[key] = ConvertValueToBoundedObject(
                        entry.Value,
                        entry.Value?.GetType(),
                        budget,
                        depth + 1);
                }
                return result;
            }

            if (value is IEnumerable enumerable)
            {
                var result = new List<object>();
                int count = 0;
                try
                {
                    foreach (object item in enumerable)
                    {
                        if (count++ >= MaxCollectionItems || budget.RemainingNodes <= 0)
                        {
                            budget.MarkTruncated();
                            result.Add(TruncationMarker("max_collection_items"));
                            break;
                        }

                        result.Add(ConvertValueToBoundedObject(
                            item,
                            item?.GetType(),
                            budget,
                            depth + 1));
                    }
                }
                catch (Exception e)
                {
                    budget.MarkTruncated();
                    result.Add(TruncationMarker("enumeration_failed", e.GetType().Name));
                }
                return result;
            }

            var objectResult = new Dictionary<string, object>
            {
                { "_type", runtimeType.FullName }
            };
            FieldInfo[] fields = runtimeType.GetFields(BindingFlags.Public | BindingFlags.Instance);
            int fieldCount = 0;
            foreach (FieldInfo field in fields)
            {
                if (fieldCount++ >= MaxObjectFields || budget.RemainingNodes <= 0)
                {
                    budget.MarkTruncated();
                    objectResult["_truncated"] = true;
                    break;
                }
                if (IsUnsafeType(field.FieldType)) continue;

                try
                {
                    object fieldValue = field.GetValue(value);
                    objectResult[field.Name] = ConvertValueToBoundedObject(
                        fieldValue,
                        field.FieldType,
                        budget,
                        depth + 1);
                }
                catch (Exception e)
                {
                    objectResult[field.Name] = new Dictionary<string, object>
                    {
                        { "_error", e.GetType().Name }
                    };
                }
            }
            return objectResult;
        }

        private static object ConvertJTokenToBoundedObject(JToken token, SerializationBudget budget, int depth)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return null;
            if (!budget.TryConsume()) return TruncationMarker("node_budget");
            if (depth > MaxSerializationDepth)
            {
                budget.MarkTruncated();
                return TruncationMarker("max_depth");
            }

            switch (token.Type)
            {
                case JTokenType.Object:
                    var objDict = new Dictionary<string, object>();
                    int propertyCount = 0;
                    foreach (var prop in ((JObject)token).Properties())
                    {
                        if (propertyCount++ >= MaxObjectFields || budget.RemainingNodes <= 0)
                        {
                            budget.MarkTruncated();
                            objDict["_truncated"] = true;
                            break;
                        }
                        objDict[prop.Name] = ConvertJTokenToBoundedObject(prop.Value, budget, depth + 1);
                    }
                    return objDict;

                case JTokenType.Array:
                    var list = new List<object>();
                    int itemCount = 0;
                    foreach (var item in (JArray)token)
                    {
                        if (itemCount++ >= MaxCollectionItems || budget.RemainingNodes <= 0)
                        {
                            budget.MarkTruncated();
                            list.Add(TruncationMarker("max_collection_items"));
                            break;
                        }
                        list.Add(ConvertJTokenToBoundedObject(item, budget, depth + 1));
                    }
                    return list;

                case JTokenType.Integer:
                    return token.ToObject<long>(); // Use long for safety
                case JTokenType.Float:
                    return token.ToObject<double>(); // Use double for safety
                case JTokenType.String:
                    string tokenText = token.ToObject<string>();
                    if (tokenText == null || tokenText.Length <= MaxStringCharacters) return tokenText;
                    budget.MarkTruncated();
                    return tokenText.Substring(0, MaxStringCharacters) + "...[truncated]";
                case JTokenType.Boolean:
                    return token.ToObject<bool>();
                case JTokenType.Date:
                    return token.ToObject<DateTime>();
                case JTokenType.Guid:
                    return token.ToObject<Guid>();
                case JTokenType.Uri:
                    return token.ToObject<Uri>();
                case JTokenType.TimeSpan:
                    return token.ToObject<TimeSpan>();
                case JTokenType.Bytes:
                    byte[] bytes = token.ToObject<byte[]>();
                    if (bytes == null || bytes.Length <= MaxCollectionItems) return bytes;
                    budget.MarkTruncated();
                    return bytes.Take(MaxCollectionItems).ToArray();
                case JTokenType.Null:
                    return null;
                case JTokenType.Undefined:
                    return null; // Treat undefined as null

                default:
                    // Fallback for simple value types not explicitly listed
                    if (token is JValue jValue && jValue.Value != null)
                    {
                        return jValue.Value;
                    }
                    // McpLog.Warn($"Unsupported JTokenType encountered: {token.Type}. Returning null.");
                    return null;
            }
        }

        private static Dictionary<string, object> TruncationMarker(string reason, string detail = null)
        {
            var marker = new Dictionary<string, object>
            {
                { "_truncated", true },
                { "reason", reason }
            };
            if (!string.IsNullOrEmpty(detail)) marker["detail"] = detail;
            return marker;
        }

        // --- Define custom JsonSerializerSettings for OUTPUT ---
        private static readonly JsonSerializerSettings _outputSerializerSettings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter>
            {
                new Vector3Converter(),
                new Vector2Converter(),
                new Vector4Converter(),
                new QuaternionConverter(),
                new ColorConverter(),
                new RectConverter(),
                new BoundsConverter(),
                new Matrix4x4Converter(), // Fix #478: Safe Matrix4x4 serialization for Cinemachine
                new UnityEngineObjectConverter() // Handles serialization of references
            },
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            // ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() } // Example if needed
        };
        private static readonly JsonSerializer _outputSerializer = JsonSerializer.Create(_outputSerializerSettings);
        // --- End Define custom JsonSerializerSettings ---

        // Helper to create JToken using the output serializer
        private static JToken CreateTokenFromValue(object value, Type type)
        {
            if (value == null) return JValue.CreateNull();

            try
            {
                // Use the pre-configured OUTPUT serializer instance
                return JToken.FromObject(value, _outputSerializer);
            }
            catch (JsonSerializationException e)
            {
                McpLog.Warn($"[GameObjectSerializer] Newtonsoft.Json Error serializing value of type {type.FullName}: {e.Message}. Skipping property/field.");
                return null; // Indicate serialization failure
            }
            catch (Exception e) // Catch other unexpected errors
            {
                McpLog.Warn($"[GameObjectSerializer] Unexpected error serializing value of type {type.FullName}: {e}. Skipping property/field.");
                return null; // Indicate serialization failure
            }
        }
    }
}
