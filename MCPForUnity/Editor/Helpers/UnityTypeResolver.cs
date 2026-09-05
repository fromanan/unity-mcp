using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Runtime.Helpers;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Compilation;
#endif

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Unified type resolution for Unity types (Components, ScriptableObjects, etc.).
    /// Extracted from ComponentResolver in ManageGameObject and ResolveType in ManageScriptableObject.
    /// Features: caching, prioritizes Player assemblies over Editor assemblies, uses TypeCache.
    /// </summary>
    public static class UnityTypeResolver
    {
        public const string InvalidTypeNameCode = "invalid_type_name";
        public const string AmbiguousTypeCode = "ambiguous_type";
        public const string TypeNotFoundCode = "type_not_found";

        private static readonly Dictionary<string, Type> CacheByFqn = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> CacheByName = new(StringComparer.Ordinal);

        public sealed class ResolutionFailure
        {
            internal ResolutionFailure(
                string code,
                string message,
                string hint,
                int candidateCount,
                string[] candidates)
            {
                Code = code;
                Message = message;
                Hint = hint;
                CandidateCount = candidateCount;
                Candidates = candidates;
            }

            public string Code { get; }
            public string Message { get; }
            public string Hint { get; }
            public int CandidateCount { get; }
            public string[] Candidates { get; }
        }

        /// <summary>
        /// Resolves a type by name, with optional base type constraint.
        /// Caches results for performance. Prefers runtime assemblies over Editor assemblies.
        /// </summary>
        /// <param name="typeName">The short name or fully-qualified name of the type</param>
        /// <param name="type">The resolved type, or null if not found</param>
        /// <param name="error">Error message if resolution failed</param>
        /// <param name="requiredBaseType">Optional base type constraint (e.g., typeof(Component))</param>
        /// <returns>True if type was resolved successfully</returns>
        public static bool TryResolve(string typeName, out Type type, out string error, Type requiredBaseType = null)
        {
            bool resolved = TryResolveDetailed(
                typeName,
                out type,
                out ResolutionFailure failure,
                requiredBaseType);
            error = failure?.Message ?? string.Empty;
            return resolved;
        }

        /// <summary>
        /// Resolves a type and returns a stable failure code plus bounded candidate details when resolution fails.
        /// </summary>
        public static bool TryResolveDetailed(
            string typeName,
            out Type type,
            out ResolutionFailure failure,
            Type requiredBaseType = null)
        {
            failure = null;
            type = null;

            if (string.IsNullOrWhiteSpace(typeName))
            {
                failure = new ResolutionFailure(
                    InvalidTypeNameCode,
                    "Type name cannot be null or empty",
                    "Provide a non-empty type name.",
                    0,
                    Array.Empty<string>());
                return false;
            }

            bool isShortName = IsShortName(typeName);

            // Check caches
            if (!isShortName && CacheByFqn.TryGetValue(typeName, out type) && PassesConstraint(type, requiredBaseType))
                return true;
            if (isShortName && CacheByName.TryGetValue(typeName, out type) && PassesConstraint(type, requiredBaseType))
                return true;

            // Try direct Type.GetType
            if (!isShortName)
            {
                type = Type.GetType(typeName, throwOnError: false);
                if (type != null && PassesConstraint(type, requiredBaseType))
                {
                    Cache(type, cacheByShortName: false);
                    return true;
                }
            }

            // Search loaded assemblies (prefer Player assemblies)
            List<Type> candidates = FindCandidates(typeName, requiredBaseType);
            if (candidates.Count == 1)
            {
                type = candidates[0];
                Cache(type, cacheByShortName: isShortName);
                return true;
            }
            if (candidates.Count > 1)
            {
                failure = CreateAmbiguityFailure(typeName, candidates);
                type = null;
                return false;
            }

#if UNITY_EDITOR
            // Last resort: TypeCache (fast index)
            if (requiredBaseType != null)
            {
                IEnumerable<Type> tc = TypeCache.GetTypesDerivedFrom(requiredBaseType)
                                                .Where(t => NamesMatch(t, typeName));
                candidates = DisambiguateByIdentity(PreferPlayer(tc).ToList());
                if (candidates.Count == 1)
                {
                    type = candidates[0];
                    Cache(type, cacheByShortName: isShortName);
                    return true;
                }
                if (candidates.Count > 1)
                {
                    failure = CreateAmbiguityFailure(typeName, candidates);
                    type = null;
                    return false;
                }
            }
#endif

            failure = new ResolutionFailure(
                TypeNotFoundCode,
                $"Type '{typeName}' not found in loaded runtime assemblies. " +
                "Use a fully-qualified name (Namespace.TypeName) and ensure the script compiled.",
                "Use a fully-qualified name and ensure the defining script compiled successfully.",
                0,
                Array.Empty<string>());
            type = null;
            return false;
        }

        /// <summary>
        /// Convenience method to resolve a Component type.
        /// </summary>
        public static Type ResolveComponent(string typeName)
        {
            if (TryResolve(typeName, out Type type, out _, typeof(Component)))
                return type;
            return null;
        }

        /// <summary>
        /// Convenience method to resolve a ScriptableObject type.
        /// </summary>
        public static Type ResolveScriptableObject(string typeName)
        {
            if (TryResolve(typeName, out Type type, out _, typeof(ScriptableObject)))
                return type;
            return null;
        }

        /// <summary>
        /// Convenience method to resolve any type without constraints.
        /// </summary>
        public static Type ResolveAny(string typeName)
        {
            if (TryResolve(typeName, out Type type, out _, null))
                return type;
            return null;
        }

        // --- Private Helpers ---

        private static bool PassesConstraint(Type type, Type requiredBaseType)
        {
            if (type == null) return false;
            if (requiredBaseType == null) return true;
            return requiredBaseType.IsAssignableFrom(type);
        }

        private static bool NamesMatch(Type t, string query) =>
            t.Name.Equals(query, StringComparison.Ordinal) ||
            (t.FullName?.Equals(query, StringComparison.Ordinal) ?? false);

        private static bool IsShortName(string query) =>
            !query.Contains(".") && !query.Contains(",");

        private static void Cache(Type t, bool cacheByShortName)
        {
            if (t == null) return;
            if (t.FullName != null) CacheByFqn[t.FullName] = t;
            if (cacheByShortName) CacheByName[t.Name] = t;
        }

        private static List<Type> FindCandidates(string query, Type requiredBaseType)
        {
            bool isShort = IsShortName(query);
            IEnumerable<System.Reflection.Assembly> loaded = UnityAssembliesCompat.GetLoadedAssemblies();

#if UNITY_EDITOR
            // Names of Player (runtime) script assemblies
            var playerAsmNames = new HashSet<string>(
                CompilationPipeline.GetAssemblies(AssembliesType.Player).Select(a => a.name),
                StringComparer.Ordinal);

            var playerAsms = loaded.Where(a => playerAsmNames.Contains(a.GetName().Name));
            var editorAsms = loaded.Except(playerAsms);
#else
            var playerAsms = loaded;
            var editorAsms = Array.Empty<System.Reflection.Assembly>();
#endif

            Func<Type, bool> match = isShort
                ? (t => t.Name.Equals(query, StringComparison.Ordinal))
                : (t => t.FullName?.Equals(query, StringComparison.Ordinal) ?? false);

            var fromPlayer = playerAsms.SelectMany(SafeGetTypes)
                                       .Where(t => PassesConstraint(t, requiredBaseType))
                                       .Where(match);
            var fromEditor = editorAsms.SelectMany(SafeGetTypes)
                                       .Where(t => PassesConstraint(t, requiredBaseType))
                                       .Where(match);

            // Prefer Player over Editor
            var candidates = fromPlayer.ToList();
            if (candidates.Count == 0)
                candidates = fromEditor.ToList();

            return DisambiguateByIdentity(candidates);
        }

        /// <summary>
        /// Collapses spurious matches that are not a genuine user-facing ambiguity:
        /// 1. De-dupe by FullName (same type surfaced via multiple assemblies / type-forwards).
        /// 2. If still >1, prefer a single PUBLIC type (e.g. public BCL List`1 over an
        ///    internal type that happens to share the short name).
        /// 3. If still >1, prefer a single core/BCL type (mscorlib / System.* / netstandard /
        ///    System.Private.CoreLib) so unqualified BCL generics resolve deterministically.
        /// Each step only narrows when it yields exactly one survivor, so genuine clashes
        /// between two public, non-BCL types (e.g. UnityEngine.UI.Button vs
        /// UnityEngine.UIElements.Button) are still reported as ambiguous.
        /// </summary>
        private static List<Type> DisambiguateByIdentity(List<Type> candidates)
        {
            if (candidates.Count <= 1) return candidates;

            // 1. De-dupe by FullName (keep first occurrence; Player-preferred ordering preserved).
            var deduped = candidates
                .GroupBy(t => t.FullName ?? t.Name, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (deduped.Count <= 1) return deduped;

            // 2. Prefer a single public type.
            var publicTypes = deduped.Where(t => t.IsPublic || t.IsNestedPublic).ToList();
            if (publicTypes.Count == 1) return publicTypes;
            var pool = publicTypes.Count > 1 ? publicTypes : deduped;

            // 3. Prefer a single core/BCL type.
            var coreTypes = pool.Where(IsCoreBclType).ToList();
            if (coreTypes.Count == 1) return coreTypes;

            return pool;
        }

        private static bool IsCoreBclType(Type t)
        {
            string asmName = t.Assembly.GetName().Name;
            if (string.IsNullOrEmpty(asmName)) return false;
            return asmName.Equals("mscorlib", StringComparison.Ordinal)
                || asmName.Equals("netstandard", StringComparison.Ordinal)
                || asmName.Equals("System.Private.CoreLib", StringComparison.Ordinal)
                || asmName.Equals("System", StringComparison.Ordinal)
                || asmName.StartsWith("System.", StringComparison.Ordinal);
        }

        private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { return rtle.Types.Where(t => t != null); }
            catch { return Enumerable.Empty<Type>(); }
        }

        private static IEnumerable<Type> PreferPlayer(IEnumerable<Type> types)
        {
#if UNITY_EDITOR
            var playerAsmNames = new HashSet<string>(
                CompilationPipeline.GetAssemblies(AssembliesType.Player).Select(a => a.name),
                StringComparer.Ordinal);

            var list = types.ToList();
            var fromPlayer = list.Where(t => playerAsmNames.Contains(t.Assembly.GetName().Name)).ToList();
            return fromPlayer.Count > 0 ? fromPlayer : list;
#else
            return types;
#endif
        }

        private static string FormatAmbiguityError(string query, List<Type> candidates)
        {
            var names = string.Join(", ", candidates.Take(5).Select(t => t.FullName));
            if (candidates.Count > 5) names += $" ... ({candidates.Count - 5} more)";
            return $"Ambiguous type reference '{query}'. Found {candidates.Count} matches: [{names}]. Use a fully-qualified name.";
        }

        private static ResolutionFailure CreateAmbiguityFailure(string query, List<Type> candidates)
        {
            string[] candidateNames = candidates
                .Take(5)
                .Select(candidate => candidate.FullName ?? candidate.Name)
                .ToArray();
            return new ResolutionFailure(
                AmbiguousTypeCode,
                FormatAmbiguityError(query, candidates),
                "Use one of the fully-qualified candidate names.",
                candidates.Count,
                candidateNames);
        }
    }
}
