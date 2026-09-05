using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MCPForUnity.Editor.Constants;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    internal static class McpLogRecord
    {
        private const long MaxLogSizeBytes = 1024 * 1024; // 1 MB
        private const int BackupCount = 2;
        private const int MaxSummaryFields = 64;
        private const int MaxCollectionItems = 64;
        private const int MaxSanitizedNodes = 256;
        private const int MaxSanitizedCharacters = 16 * 1024;
        private const int MaxStringCharacters = 2048;
        private const int MaxErrorCharacters = 4096;
        private const int MaxLabelCharacters = 256;
        private const int MaxDepth = 6;

        private static readonly string ProjectRoot =
            Directory.GetParent(Application.dataPath)?.FullName
            ?? Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static readonly string LogDir =
            Path.Combine(ProjectRoot, "Library", "MCPForUnity", "Logs");
        private static readonly string LogPath = Path.Combine(LogDir, "mcp.log");
        private static readonly string ErrorLogPath = Path.Combine(LogDir, "mcpError.log");
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static readonly Regex BearerTokenPattern = new(
            @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SensitiveAssignmentPattern = new(
            @"(?i)((?:api[_-]?key|access[_-]?key|private[_-]?key|refresh[_-]?token|token|secret|password|authorization|cookie|credential|connection[_-]?string)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly string[] SensitiveNameFragments =
        {
            "apikey",
            "token",
            "accesstoken",
            "refreshtoken",
            "authtoken",
            "authorization",
            "password",
            "secret",
            "credential",
            "privatekey",
            "accesskey",
            "connectionstring",
            "cookie"
        };

        private static bool _sessionStarted;
        private static bool _logDirectoryReady;
        private static readonly object _logLock = new();
        private static volatile bool _isEnabledCached;
        private static volatile bool _includeParameterValuesCached;

        [InitializeOnLoadMethod]
        private static void RefreshFromPrefs()
        {
            _isEnabledCached = EditorPrefs.GetBool(EditorPrefKeys.LogRecordEnabled, false);
            _includeParameterValuesCached = EditorPrefs.GetBool(
                EditorPrefKeys.LogRecordIncludeParameterValues,
                false);
        }

        internal static bool IsEnabled
        {
            get => _isEnabledCached;
            set
            {
                EditorPrefs.SetBool(EditorPrefKeys.LogRecordEnabled, value);
                _isEnabledCached = value;
            }
        }

        internal static bool IncludeParameterValues
        {
            get => _includeParameterValuesCached;
            set
            {
                EditorPrefs.SetBool(EditorPrefKeys.LogRecordIncludeParameterValues, value);
                _includeParameterValuesCached = value;
            }
        }

        internal static string LogDirectory => LogDir;

        internal static void Log(
            string commandType,
            JObject parameters,
            string type,
            string status,
            long durationMs,
            string error = null)
        {
            if (!IsEnabled) return;

            try
            {
                JObject entry = CreateEntry(
                    commandType,
                    parameters,
                    type,
                    status,
                    durationMs,
                    error,
                    IncludeParameterValues);
                string line = entry.ToString(Formatting.None);

                lock (_logLock)
                {
                    EnsureLogDirectory();
                    if (!_sessionStarted)
                    {
                        var sessionEntry = new JObject
                        {
                            ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            ["event"] = "session_start",
                            ["unity"] = BoundAndRedact(Application.unityVersion, MaxLabelCharacters)
                        };
                        RotateAndAppend(LogPath, sessionEntry.ToString(Formatting.None));
                        _sessionStarted = true;
                    }

                    RotateAndAppend(LogPath, line);

                    if (string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        RotateAndAppend(ErrorLogPath, line);
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[McpLogRecord] Failed to write log: {ex.Message}");
            }
        }

        internal static JObject CreateEntry(
            string commandType,
            JObject parameters,
            string type,
            string status,
            long durationMs,
            string error,
            bool includeParameterValues)
        {
            var entry = new JObject
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["tool"] = BoundAndRedact(commandType, MaxLabelCharacters),
                ["type"] = BoundAndRedact(type, MaxLabelCharacters),
                ["status"] = BoundAndRedact(status, MaxLabelCharacters),
                ["ms"] = durationMs
            };

            string action = parameters?.Value<string>("action");
            if (!string.IsNullOrEmpty(action))
            {
                entry["action"] = BoundAndRedact(action, MaxLabelCharacters);
            }

            if (parameters != null)
            {
                entry["params"] = includeParameterValues
                    ? SanitizeToken(parameters, new SanitizationBudget(), 0, null)
                    : BuildParameterSummary(parameters);
            }

            if (!string.IsNullOrEmpty(error))
            {
                entry["error"] = BoundAndRedact(error, MaxErrorCharacters);
            }

            return entry;
        }

        private static JObject BuildParameterSummary(JObject parameters)
        {
            var fields = new JArray();
            int included = 0;
            foreach (JProperty property in parameters.Properties())
            {
                if (included >= MaxSummaryFields) break;
                fields.Add(new JObject
                {
                    ["name"] = BoundAndRedact(property.Name, MaxLabelCharacters),
                    ["type"] = property.Value?.Type.ToString() ?? JTokenType.Null.ToString(),
                    ["size"] = GetLogicalSize(property.Value),
                    ["sizeUnit"] = GetLogicalSizeUnit(property.Value)
                });
                included++;
            }

            var summary = new JObject
            {
                ["count"] = parameters.Count,
                ["fields"] = fields
            };
            if (parameters.Count > included)
            {
                summary["omitted"] = parameters.Count - included;
            }
            return summary;
        }

        private static int GetLogicalSize(JToken token)
        {
            return token switch
            {
                null => 0,
                JValue value when value.Type == JTokenType.String =>
                    value.Value<string>()?.Length ?? 0,
                JArray array => array.Count,
                JObject obj => obj.Count,
                _ => 1
            };
        }

        private static string GetLogicalSizeUnit(JToken token)
        {
            return token switch
            {
                JValue value when value.Type == JTokenType.String => "characters",
                JArray => "items",
                JObject => "properties",
                _ => "value"
            };
        }

        private static JToken SanitizeToken(
            JToken token,
            SanitizationBudget budget,
            int depth,
            string propertyName)
        {
            if (IsSensitiveName(propertyName))
            {
                return new JValue("[REDACTED]");
            }
            if (token == null || token.Type == JTokenType.Null)
            {
                return JValue.CreateNull();
            }
            if (depth >= MaxDepth || !budget.TryConsumeNode())
            {
                return new JValue("[TRUNCATED]");
            }

            if (token is JObject obj)
            {
                var sanitized = new JObject();
                int included = 0;
                foreach (JProperty property in obj.Properties())
                {
                    if (included >= MaxCollectionItems || !budget.HasNodes) break;
                    string name = BoundAndRedact(property.Name, MaxLabelCharacters);
                    sanitized[name] = SanitizeToken(
                        property.Value,
                        budget,
                        depth + 1,
                        property.Name);
                    included++;
                }
                if (obj.Count > included)
                {
                    sanitized["_mcpOmitted"] = obj.Count - included;
                }
                return sanitized;
            }

            if (token is JArray array)
            {
                var sanitized = new JArray();
                int included = 0;
                foreach (JToken item in array)
                {
                    if (included >= MaxCollectionItems || !budget.HasNodes) break;
                    sanitized.Add(SanitizeToken(item, budget, depth + 1, null));
                    included++;
                }
                if (array.Count > included)
                {
                    sanitized.Add(new JObject { ["_mcpOmitted"] = array.Count - included });
                }
                return sanitized;
            }

            if (token.Type == JTokenType.String || token.Type == JTokenType.Raw)
            {
                return new JValue(budget.TakeCharacters(
                    BoundAndRedact(token.Value<string>(), MaxStringCharacters)));
            }
            if (token.Type == JTokenType.Bytes)
            {
                int length = token.Value<byte[]>()?.Length ?? 0;
                return new JValue($"[BINARY {length} bytes]");
            }

            return token.DeepClone();
        }

        private static bool IsSensitiveName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string normalized = name.Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(".", string.Empty);
            foreach (string fragment in SensitiveNameFragments)
            {
                if (normalized.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string BoundAndRedact(string value, int maxCharacters)
        {
            if (value == null) return null;
            string redacted = BearerTokenPattern.Replace(value, "Bearer [REDACTED]");
            redacted = SensitiveAssignmentPattern.Replace(redacted, "$1[REDACTED]");
            return redacted.Length <= maxCharacters
                ? redacted
                : redacted.Substring(0, maxCharacters) + "…";
        }

        private static void EnsureLogDirectory()
        {
            if (_logDirectoryReady) return;
            Directory.CreateDirectory(LogDir);
            _logDirectoryReady = true;
        }

        private static void RotateAndAppend(string path, string line)
        {
            string text = line + Environment.NewLine;
            RotateIfNeeded(path, Utf8NoBom.GetByteCount(text));
            File.AppendAllText(path, text, Utf8NoBom);
        }

        private static void RotateIfNeeded(string path, int pendingBytes)
        {
            try
            {
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length + pendingBytes <= MaxLogSizeBytes) return;

                for (int index = BackupCount - 1; index >= 1; index--)
                {
                    string source = $"{path}.{index}";
                    if (!File.Exists(source)) continue;
                    string destination = $"{path}.{index + 1}";
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(source, destination);
                }

                string firstBackup = $"{path}.1";
                if (File.Exists(firstBackup)) File.Delete(firstBackup);
                File.Move(path, firstBackup);
            }
            catch
            {
                // Best-effort rotation
            }
        }

        private sealed class SanitizationBudget
        {
            private int _nodesRemaining = MaxSanitizedNodes;
            private int _charactersRemaining = MaxSanitizedCharacters;

            internal bool HasNodes => _nodesRemaining > 0;

            internal bool TryConsumeNode()
            {
                if (_nodesRemaining <= 0) return false;
                _nodesRemaining--;
                return true;
            }

            internal string TakeCharacters(string value)
            {
                if (string.IsNullOrEmpty(value)) return value;
                if (_charactersRemaining <= 0) return "[TRUNCATED]";
                int count = Math.Min(value.Length, _charactersRemaining);
                _charactersRemaining -= count;
                return count == value.Length
                    ? value
                    : value.Substring(0, count) + "…";
            }
        }
    }
}
