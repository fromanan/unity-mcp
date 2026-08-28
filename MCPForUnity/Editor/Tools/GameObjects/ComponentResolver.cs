#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Component resolver that delegates to UnityTypeResolver.
    /// Kept for backwards compatibility.
    /// </summary>
    internal static class ComponentResolver
    {
        private const int MaxSuggestionCacheEntries = 128;
        private static readonly ConcurrentDictionary<Type, string[]> ComponentPropertyCache = new();
        private static readonly object SuggestionCacheLock = new();
        private static readonly Dictionary<string, LinkedListNode<SuggestionCacheEntry>> PropertySuggestionCache = new();
        private static readonly LinkedList<SuggestionCacheEntry> PropertySuggestionLru = new();

        /// <summary>
        /// Resolve a Component/MonoBehaviour type by short or fully-qualified name.
        /// Delegates to UnityTypeResolver.TryResolve with Component constraint.
        /// </summary>
        public static bool TryResolve(string nameOrFullName, out Type type, out string error)
        {
            return UnityTypeResolver.TryResolve(nameOrFullName, out type, out error, typeof(Component));
        }

        /// <summary>
        /// Gets all accessible property and field names from a component type.
        /// </summary>
        public static List<string> GetAllComponentProperties(Type componentType)
        {
            if (componentType == null) return new List<string>();

            string[] cached = ComponentPropertyCache.GetOrAdd(componentType, BuildComponentProperties);
            return new List<string>(cached);
        }

        private static string[] BuildComponentProperties(Type componentType)
        {
            IEnumerable<string> properties = componentType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.CanWrite)
                .Select(property => property.Name);

            IEnumerable<string> fields = componentType
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(field => !field.IsInitOnly && !field.IsLiteral)
                .Select(field => field.Name);

            // Also include SerializeField private fields (common in Unity)
            IEnumerable<string> serializeFields = componentType
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(field => field.GetCustomAttribute<SerializeField>() != null)
                .Select(field => field.Name);

            return properties
                .Concat(fields)
                .Concat(serializeFields)
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Suggests the most likely property matches for a user's input using fuzzy matching.
        /// Uses Levenshtein distance, substring matching, and common naming pattern heuristics.
        /// </summary>
        public static List<string> GetFuzzyPropertySuggestions(string userInput, List<string> availableProperties)
        {
            if (string.IsNullOrWhiteSpace(userInput) || availableProperties == null || availableProperties.Count == 0)
                return new List<string>();

            string normalizedInput = NormalizeName(userInput);
            string cacheKey = BuildSuggestionCacheKey(normalizedInput, availableProperties);
            if (TryGetCachedSuggestions(cacheKey, out List<string> cached))
                return cached;

            try
            {
                List<string> suggestions = GetRuleBasedSuggestions(userInput, availableProperties);
                StoreCachedSuggestions(cacheKey, suggestions);
                return suggestions;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[Property Matching] Error getting suggestions for '{userInput}': {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Rule-based suggestions that mimic AI behavior for property matching.
        /// This provides immediate value while we could add real AI integration later.
        /// </summary>
        private static List<string> GetRuleBasedSuggestions(string userInput, List<string> availableProperties)
        {
            string cleanedInput = NormalizeName(userInput);
            string[] inputWords = userInput
                .ToLowerInvariant()
                .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            int threshold = Math.Max(2, cleanedInput.Length / 4);
            List<PropertyScore> matches = new();

            foreach (string property in availableProperties)
            {
                if (string.IsNullOrWhiteSpace(property))
                    continue;

                string cleanedProperty = NormalizeName(property);
                if (cleanedProperty == cleanedInput)
                {
                    matches.Add(new PropertyScore(property, 0));
                    continue;
                }

                bool containsAllWords = inputWords.Length > 0
                    && inputWords.All(word => cleanedProperty.Contains(word));
                int distance = LevenshteinDistance(cleanedInput, cleanedProperty, threshold);
                if (containsAllWords || distance <= threshold)
                {
                    int score = containsAllWords
                        ? LevenshteinDistance(cleanedInput, cleanedProperty, Math.Max(cleanedInput.Length, cleanedProperty.Length))
                        : distance;
                    matches.Add(new PropertyScore(property, score));
                }
            }

            return matches
                .OrderBy(match => match.Score)
                .ThenBy(match => match.Property, StringComparer.Ordinal)
                .Take(3)
                .Select(match => match.Property)
                .ToList();
        }

        private static string NormalizeName(string value)
        {
            return value
                .ToLowerInvariant()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
        }

        private static string BuildSuggestionCacheKey(string normalizedInput, List<string> availableProperties)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offsetBasis;
            foreach (string property in availableProperties)
            {
                string value = property ?? string.Empty;
                foreach (char character in value)
                {
                    hash ^= character;
                    hash *= prime;
                }
                hash ^= 0xFF;
                hash *= prime;
            }
            return $"{normalizedInput}:{availableProperties.Count}:{hash:X16}";
        }

        private static bool TryGetCachedSuggestions(string cacheKey, out List<string> suggestions)
        {
            lock (SuggestionCacheLock)
            {
                if (!PropertySuggestionCache.TryGetValue(cacheKey, out LinkedListNode<SuggestionCacheEntry> node))
                {
                    suggestions = null;
                    return false;
                }

                PropertySuggestionLru.Remove(node);
                PropertySuggestionLru.AddLast(node);
                suggestions = new List<string>(node.Value.Suggestions);
                return true;
            }
        }

        private static void StoreCachedSuggestions(string cacheKey, List<string> suggestions)
        {
            lock (SuggestionCacheLock)
            {
                if (PropertySuggestionCache.TryGetValue(cacheKey, out LinkedListNode<SuggestionCacheEntry> existing))
                {
                    PropertySuggestionLru.Remove(existing);
                }

                SuggestionCacheEntry entry = new(cacheKey, suggestions.ToArray());
                LinkedListNode<SuggestionCacheEntry> node = PropertySuggestionLru.AddLast(entry);
                PropertySuggestionCache[cacheKey] = node;

                while (PropertySuggestionCache.Count > MaxSuggestionCacheEntries)
                {
                    LinkedListNode<SuggestionCacheEntry> oldest = PropertySuggestionLru.First;
                    if (oldest == null)
                        break;
                    PropertySuggestionLru.RemoveFirst();
                    PropertySuggestionCache.Remove(oldest.Value.Key);
                }
            }
        }

        /// <summary>
        /// Calculates Levenshtein distance between two strings for similarity matching.
        /// </summary>
        private static int LevenshteinDistance(string s1, string s2, int maxDistance)
        {
            if (string.IsNullOrEmpty(s1)) return s2?.Length ?? 0;
            if (string.IsNullOrEmpty(s2)) return s1.Length;
            if (Math.Abs(s1.Length - s2.Length) > maxDistance) return maxDistance + 1;

            int[] previous = new int[s2.Length + 1];
            int[] current = new int[s2.Length + 1];
            for (int column = 0; column <= s2.Length; column++)
            {
                previous[column] = column;
            }

            for (int row = 1; row <= s1.Length; row++)
            {
                current[0] = row;
                int rowMinimum = current[0];
                for (int column = 1; column <= s2.Length; column++)
                {
                    int cost = s2[column - 1] == s1[row - 1] ? 0 : 1;
                    current[column] = Math.Min(
                        Math.Min(previous[column] + 1, current[column - 1] + 1),
                        previous[column - 1] + cost);
                    rowMinimum = Math.Min(rowMinimum, current[column]);
                }

                if (rowMinimum > maxDistance)
                    return maxDistance + 1;

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[s2.Length];
        }

        private sealed class SuggestionCacheEntry
        {
            public SuggestionCacheEntry(string key, string[] suggestions)
            {
                Key = key;
                Suggestions = suggestions;
            }

            public string Key { get; }
            public string[] Suggestions { get; }
        }

        private readonly struct PropertyScore
        {
            public PropertyScore(string property, int score)
            {
                Property = property;
                Score = score;
            }

            public string Property { get; }
            public int Score { get; }
        }
    }
}
