using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Runtime.Helpers;
using UnityEditor;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Shared domain-lifetime cache for attribute-based Unity type discovery.
    /// </summary>
    internal static class AttributedTypeCatalog
    {
        private static readonly object CacheLock = new();
        private static readonly Dictionary<Type, Type[]> Cache = new();

        public static IReadOnlyList<Type> GetTypesWithAttribute<TAttribute>()
            where TAttribute : Attribute
        {
            Type attributeType = typeof(TAttribute);
            lock (CacheLock)
            {
                if (Cache.TryGetValue(attributeType, out Type[] cached))
                {
                    return cached;
                }
            }

            Type[] discovered = DiscoverTypes<TAttribute>();
            lock (CacheLock)
            {
                Cache[attributeType] = discovered;
            }
            return discovered;
        }

        public static void Clear()
        {
            lock (CacheLock)
            {
                Cache.Clear();
            }
        }

        private static Type[] DiscoverTypes<TAttribute>()
            where TAttribute : Attribute
        {
            try
            {
                List<Type> typeCacheResults = TypeCache
                    .GetTypesWithAttribute<TAttribute>()
                    .Where(type => type != null)
                    .Distinct()
                    .ToList();
                if (typeCacheResults.Count > 0)
                {
                    return typeCacheResults.ToArray();
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"TypeCache scan for {typeof(TAttribute).Name} failed: {ex.Message}");
            }

            List<Type> fallback = new();
            foreach (Assembly assembly in UnityAssembliesCompat.GetLoadedAssemblies())
            {
                if (assembly == null || assembly.IsDynamic)
                    continue;

                Type[] assemblyTypes;
                try
                {
                    assemblyTypes = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    assemblyTypes = ex.Types.Where(type => type != null).ToArray();
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Failed to reflect types from assembly {assembly.FullName}: {ex.Message}");
                    continue;
                }

                foreach (Type type in assemblyTypes)
                {
                    try
                    {
                        if (type.GetCustomAttribute<TAttribute>() != null)
                        {
                            fallback.Add(type);
                        }
                    }
                    catch
                    {
                        // Type metadata can be incomplete during domain reload.
                    }
                }
            }
            return fallback.Distinct().ToArray();
        }
    }
}
