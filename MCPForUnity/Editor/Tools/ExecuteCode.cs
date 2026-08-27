using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Microsoft.CSharp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("execute_code", AutoRegister = false, Group = "scripting_ext")]
    public static class ExecuteCode
    {
        private const int MaxCodeLength = 50000;
        private const int MaxHistoryEntries = 50;
        private const int MaxHistoryCodePreview = 500;
        internal const int MaxUniqueCompilationsPerDomain = 32;
        internal const int WrapperLineOffset = 10;
        private const string WrapperClassName = "MCPDynamicCode";
        private const string WrapperMethodName = "Execute";

        private const string ActionExecute = "execute";
        private const string ActionGetHistory = "get_history";
        private const string ActionGetStatus = "get_status";
        private const string ActionClearHistory = "clear_history";
        private const string ActionReplay = "replay";

        private static readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private static readonly Dictionary<string, CompiledSnippet> _compiledSnippets =
            new Dictionary<string, CompiledSnippet>(StringComparer.Ordinal);
        private static readonly SemaphoreSlim _executionGate = new SemaphoreSlim(1, 1);
        private static readonly MethodInfo _serializeValueMethod = typeof(GameObjectSerializer).GetMethod(
            "SerializeValue",
            BindingFlags.Public | BindingFlags.Static);
        private static readonly FieldInfo _gameObjectOutputSerializerField = typeof(GameObjectSerializer).GetField(
            "_outputSerializer",
            BindingFlags.NonPublic | BindingFlags.Static);
        private static string[] _cachedAssemblyPaths;
        private static int _uniqueCompilationCount;
        private static int _cacheHitCount;
        private static int _cacheMissCount;

        [UnityEditor.InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            _cachedAssemblyPaths = null;
            _compiledSnippets.Clear();
            _uniqueCompilationCount = 0;
            _cacheHitCount = 0;
            _cacheMissCount = 0;
            RoslynCompiler.ResetCache();
        }

        private static readonly HashSet<string> _blockedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.IO.File.Delete",
            "System.IO.Directory.Delete",
            "FileUtil.DeleteFileOrDirectory",
            "AssetDatabase.DeleteAsset",
            "AssetDatabase.MoveAssetToTrash",
            "AssetDatabase.LoadAssetAtPath",
            "EditorApplication.Exit",
            "Process.Start",
            "Process.Kill",
            "while(true)",
            "while (true)",
            "for(;;)",
            "for (;;)",
        };

        private static readonly Dictionary<string, string> _blockedDetachedWorkPatterns =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".ContinueWith("] = "Return the Task or Task<T> directly so execute_code can await it.",
                ["Task.Run("] = "Return an existing Task from a Unity-safe API instead of starting detached worker work.",
                ["Task.Factory.StartNew("] = "Return an existing Task from a Unity-safe API instead of starting detached worker work.",
                ["ThreadPool.QueueUserWorkItem("] = "Detached ThreadPool work can outlive the MCP request.",
                ["new Thread("] = "Detached threads can outlive the MCP request.",
                ["async void"] = "Use a returned Task or Task<T> so execute_code can await completion.",
                ["EditorApplication.delayCall +="] = "Delayed callbacks can outlive the MCP request and retain its generated assembly.",
                ["EditorApplication.update +="] = "Editor update callbacks can outlive the MCP request and retain its generated assembly.",
            };

        public static async Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            ToolParams p = new ToolParams(@params);
            Result<string> actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            string action = actionResult.Value.ToLowerInvariant();

            switch (action)
            {
                case ActionExecute:
                {
                    return await HandleExecuteAsync(@params).ConfigureAwait(true);
                }
                case ActionGetHistory:
                {
                    return HandleGetHistory(@params);
                }
                case ActionGetStatus:
                {
                    return HandleGetStatus();
                }
                case ActionClearHistory:
                {
                    return HandleClearHistory();
                }
                case ActionReplay:
                {
                    return await HandleReplayAsync(@params).ConfigureAwait(true);
                }
                default:
                {
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions: {ActionExecute}, {ActionGetHistory}, {ActionGetStatus}, {ActionClearHistory}, {ActionReplay}");
                }
            }
        }

        private static async Task<object> HandleExecuteAsync(JObject @params)
        {
            string code = @params["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
                return new ErrorResponse("Required parameter 'code' is missing or empty.");

            if (code.Length > MaxCodeLength)
                return new ErrorResponse($"Code exceeds maximum length of {MaxCodeLength} characters.");

            bool safetyChecks = @params["safety_checks"]?.Value<bool>() ?? true;
            string compiler = @params["compiler"]?.ToString()?.ToLowerInvariant() ?? "auto";

            if (safetyChecks)
            {
                string violation = CheckBlockedPatterns(code);
                if (violation != null)
                    return new ErrorResponse($"Blocked pattern detected: {violation}");
            }

            await _executionGate.WaitAsync().ConfigureAwait(true);
            try
            {
                DateTime startTime = DateTime.UtcNow;
                object result = await CompileAndExecuteAsync(code, compiler).ConfigureAwait(true);
                double elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

                AddToHistory(code, result, elapsed, safetyChecks, compiler);
                return result;
            }
            catch (Exception e)
            {
                McpLog.Error($"[ExecuteCode] Execution failed: {e}");
                ErrorResponse errorResult = new ErrorResponse($"Execution failed: {e.Message}");
                AddToHistory(code, errorResult, 0, safetyChecks, compiler);
                return errorResult;
            }
            finally
            {
                _executionGate.Release();
            }
        }

        private static object HandleGetHistory(JObject @params)
        {
            int limit = @params["limit"]?.Value<int>() ?? 10;
            limit = Math.Clamp(limit, 1, MaxHistoryEntries);

            if (_history.Count == 0)
                return new SuccessResponse("No execution history.", new { total = 0, entries = new object[0] });

            List<HistoryEntry> entries = _history.Skip(Math.Max(0, _history.Count - limit)).ToList();
            return new SuccessResponse($"Returning {entries.Count} of {_history.Count} history entries.", new
            {
                total = _history.Count,
                entries = entries.Select((e, i) => new
                {
                    index = _history.Count - entries.Count + i,
                    codePreview = e.code.Length > MaxHistoryCodePreview
                        ? e.code.Substring(0, MaxHistoryCodePreview) + "..."
                        : e.code,
                    e.success,
                    e.resultPreview,
                    e.elapsedMs,
                    e.timestamp,
                    e.safetyChecksEnabled,
                    e.compiler,
                }).ToList(),
            });
        }

        private static object HandleGetStatus()
        {
            return new SuccessResponse("Returning execute_code compilation status.", CreateCompilationStatus());
        }

        private static object HandleClearHistory()
        {
            int count = _history.Count;
            _history.Clear();
            return new SuccessResponse($"Cleared {count} history entries.");
        }

        private static async Task<object> HandleReplayAsync(JObject @params)
        {
            if (_history.Count == 0)
                return new ErrorResponse("No execution history to replay.");

            int? index = @params["index"]?.Value<int>();
            if (index == null || index < 0 || index >= _history.Count)
                return new ErrorResponse($"Invalid history index. Valid range: 0-{_history.Count - 1}");

            HistoryEntry entry = _history[index.Value];
            JObject replayParams = JObject.FromObject(new
            {
                action = ActionExecute,
                code = entry.code,
                safety_checks = entry.safetyChecksEnabled,
                compiler = entry.compiler ?? "auto",
            });
            return await HandleExecuteAsync(replayParams).ConfigureAwait(true);
        }

        // ──────────────────── Compilation ────────────────────

        private static async Task<object> CompileAndExecuteAsync(string code, string compiler)
        {
            if (!TryResolveCompiler(compiler, out string compilerUsed, out ErrorResponse compilerError))
                return compilerError;

            string wrappedSource = WrapUserCode(code);
            string cacheKey = CreateCompilationCacheKey(wrappedSource, compilerUsed);
            if (_compiledSnippets.TryGetValue(cacheKey, out CompiledSnippet cachedSnippet))
            {
                _cacheHitCount++;
                return await InvokeCompiledAsync(cachedSnippet, true).ConfigureAwait(true);
            }

            _cacheMissCount++;
            if (_uniqueCompilationCount >= MaxUniqueCompilationsPerDomain)
            {
                return new ErrorResponse(
                    $"The execute_code domain limit of {MaxUniqueCompilationsPerDomain} unique successful compilations has been reached. " +
                    "Reuse a cached snippet, use a purpose-built tool, or explicitly reload the Unity domain before compiling more code.",
                    CreateCompilationStatus());
            }

            string[] assemblyPaths = GetAssemblyPaths();
            Assembly compiled = CompileSource(wrappedSource, assemblyPaths, compilerUsed, out List<string> errors);
            if (compiled == null)
            {
                return new ErrorResponse("Compilation failed", new
                {
                    errors = OffsetErrors(errors),
                    compiler = compilerUsed,
                    compilation = CreateCompilationStatus(),
                });
            }

            _uniqueCompilationCount++;
            Type type = compiled.GetType(WrapperClassName);
            if (type == null)
            {
                return new ErrorResponse(
                    "Internal error: failed to find compiled type.",
                    CreateCompilationStatus());
            }

            MethodInfo method = type.GetMethod(WrapperMethodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                return new ErrorResponse(
                    "Internal error: failed to find Execute method.",
                    CreateCompilationStatus());
            }

            CompiledSnippet snippet = new CompiledSnippet(method, compilerUsed);
            _compiledSnippets.Add(cacheKey, snippet);
            return await InvokeCompiledAsync(snippet, false).ConfigureAwait(true);
        }

        private static bool TryResolveCompiler(
            string requestedCompiler,
            out string compilerUsed,
            out ErrorResponse error)
        {
            compilerUsed = null;
            error = null;

            switch (requestedCompiler)
            {
                case "roslyn":
                {
                    if (!RoslynCompiler.IsAvailable)
                    {
                        error = new ErrorResponse(
                            "Roslyn (Microsoft.CodeAnalysis) is not available. Install it via NuGet or use compiler='codedom'.");
                        return false;
                    }

                    compilerUsed = "roslyn";
                    return true;
                }
                case "codedom":
                {
                    compilerUsed = "codedom";
                    return true;
                }
                case "auto":
                {
                    compilerUsed = RoslynCompiler.IsAvailable ? "roslyn" : "codedom";
                    return true;
                }
                default:
                {
                    error = new ErrorResponse(
                        $"Unknown compiler: '{requestedCompiler}'. Valid compilers: auto, roslyn, codedom.");
                    return false;
                }
            }
        }

        private static Assembly CompileSource(
            string wrappedSource,
            string[] assemblyPaths,
            string compiler,
            out List<string> errors)
        {
            switch (compiler)
            {
                case "roslyn":
                {
                    return RoslynCompiler.Compile(wrappedSource, assemblyPaths, out errors);
                }
                case "codedom":
                {
                    return CodeDomCompile(wrappedSource, assemblyPaths, out errors);
                }
                default:
                {
                    errors = new List<string> { $"Unsupported compiler '{compiler}'." };
                    return null;
                }
            }
        }

        private static async Task<object> InvokeCompiledAsync(CompiledSnippet snippet, bool cacheHit)
        {
            object result = null;
            Exception executionError = null;

            try
            {
                result = snippet.Method.Invoke(null, null);
                if (result is Task task)
                    result = await AwaitTaskResultAsync(task).ConfigureAwait(true);
            }
            catch (TargetInvocationException tie)
            {
                executionError = tie.InnerException ?? tie;
            }
            catch (Exception e)
            {
                executionError = e;
            }

            if (executionError != null)
            {
                return new ErrorResponse($"Runtime error: {executionError.Message}", new
                {
                    exceptionType = executionError.GetType().Name,
                    stackTrace = executionError.StackTrace,
                    compiler = snippet.Compiler,
                    cacheHit,
                    compilation = CreateCompilationStatus(),
                });
            }

            if (result != null)
            {
                return new SuccessResponse("Code executed successfully.", new
                {
                    result = SerializeResult(result),
                    compiler = snippet.Compiler,
                    cacheHit,
                    compilation = CreateCompilationStatus(),
                });
            }

            return new SuccessResponse("Code executed successfully.", new
            {
                compiler = snippet.Compiler,
                cacheHit,
                compilation = CreateCompilationStatus(),
            });
        }

        private static async Task<object> AwaitTaskResultAsync(Task task)
        {
            await task.ConfigureAwait(true);

            Type taskType = task.GetType();
            if (!taskType.IsGenericType)
                return null;

            PropertyInfo resultProperty = taskType.GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            return resultProperty?.GetValue(task);
        }

        private static List<string> OffsetErrors(List<string> errors)
        {
            // Errors already have line numbers adjusted by the compiler-specific code
            return errors;
        }

        // ──────────────────── CodeDom compiler ────────────────────

        private static Assembly CodeDomCompile(string source, string[] assemblyPaths, out List<string> errors)
        {
            errors = new List<string>();

            // CodeDom needs the netstandard-aware filtered paths
            var filtered = FilterAssemblyPathsForCodeDom(assemblyPaths);

            // CSharpCodeProvider turns every ReferencedAssemblies entry into a literal /r:"..." flag
            // on the csc command line. Projects with ~100+ asmdefs blow past Windows' 32 KB
            // CreateProcess argument limit and fail with "The filename or extension is too long."
            // Route references through a response file (@responsefile is supported by both mcs and
            // Roslyn csc) so we pass exactly one short argument regardless of reference count.
            string responseFilePath = Path.Combine(Path.GetTempPath(), $"mcp-codedom-{Guid.NewGuid():N}.rsp");

            try
            {
                using (var writer = new StreamWriter(responseFilePath, append: false, Encoding.UTF8))
                {
                    foreach (var path in filtered)
                    {
                        writer.Write("/r:\"");
                        writer.Write(path);
                        writer.WriteLine("\"");
                    }
                }

                using (var provider = new CSharpCodeProvider())
                {
                    var parameters = new CompilerParameters
                    {
                        GenerateInMemory = true,
                        GenerateExecutable = false,
                        TreatWarningsAsErrors = false,
                        CompilerOptions = "@\"" + responseFilePath + "\"",
                    };

                    var results = provider.CompileAssemblyFromSource(parameters, source);

                    if (results.Errors.HasErrors)
                    {
                        foreach (CompilerError error in results.Errors)
                        {
                            if (!error.IsWarning)
                            {
                                int userLine = Math.Max(1, error.Line - WrapperLineOffset);
                                errors.Add($"Line {userLine}: {error.ErrorText}");
                            }
                        }
                        return null;
                    }

                    return results.CompiledAssembly;
                }
            }
            finally
            {
                try { if (File.Exists(responseFilePath)) File.Delete(responseFilePath); }
                catch { /* best effort */ }
            }
        }

        // CSharpCodeProvider can't resolve type-forwarding, so when netstandard.dll is loaded
        // alongside mscorlib/System.Runtime/System.Collections, types like List<T> appear in
        // multiple assemblies causing "type defined multiple times" errors.
        private static readonly HashSet<string> _codedomDuplicateAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib",
            "System.Runtime",
            "System.Private.CoreLib",
            "System.Collections",
        };

        private static string[] FilterAssemblyPathsForCodeDom(string[] allPaths)
        {
            bool hasNetstandard = allPaths.Any(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), "netstandard", StringComparison.OrdinalIgnoreCase));

            if (!hasNetstandard)
                return allPaths;

            return allPaths.Where(p =>
                !_codedomDuplicateAssemblies.Contains(Path.GetFileNameWithoutExtension(p))).ToArray();
        }

        // ──────────────────── Shared helpers ────────────────────

        private static string WrapUserCode(string code)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine($"public static class {WrapperClassName}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static object {WrapperMethodName}()");
            sb.AppendLine("    {");
            sb.AppendLine(code);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string[] GetAssemblyPaths()
        {
            if (_cachedAssemblyPaths == null)
                _cachedAssemblyPaths = ResolveAssemblyPaths();
            return _cachedAssemblyPaths;
        }

        private static string[] ResolveAssemblyPaths()
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Assembly assembly in UnityAssembliesCompat.GetLoadedAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic) continue;
                    string location = assembly.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    if (!File.Exists(location)) continue;
                    paths.Add(location);
                }
                catch (NotSupportedException)
                {
                    // Some assemblies don't support Location property
                }
            }

            string[] result = new string[paths.Count];
            paths.CopyTo(result);
            Array.Sort(result, StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static string CreateCompilationCacheKey(string wrappedSource, string compiler)
        {
            byte[] sourceBytes = Encoding.UTF8.GetBytes(compiler + "\0" + wrappedSource);
            using (SHA256 sha256 = SHA256.Create())
            {
                return Convert.ToBase64String(sha256.ComputeHash(sourceBytes));
            }
        }

        private static object CreateCompilationStatus()
        {
            return new
            {
                uniqueCompilations = _uniqueCompilationCount,
                compilationLimit = MaxUniqueCompilationsPerDomain,
                remainingUniqueCompilations = Math.Max(
                    0,
                    MaxUniqueCompilationsPerDomain - _uniqueCompilationCount),
                cachedSnippets = _compiledSnippets.Count,
                cacheHits = _cacheHitCount,
                cacheMisses = _cacheMissCount,
                executionInProgress = _executionGate.CurrentCount == 0,
                roslynMetadataReferencesCached = RoslynCompiler.CachedMetadataReferenceCount,
            };
        }

        private static string CheckBlockedPatterns(string code)
        {
            foreach (KeyValuePair<string, string> blockedPattern in _blockedDetachedWorkPatterns)
            {
                if (code.IndexOf(blockedPattern.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return $"Code contains detached work pattern: '{blockedPattern.Key}'. " +
                           blockedPattern.Value +
                           " Disable safety checks with safety_checks=false only when detached lifetime is explicitly intended.";
                }
            }

            foreach (string pattern in _blockedPatterns)
            {
                if (code.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"Code contains blocked pattern: '{pattern}'. Disable safety checks with safety_checks=false if this is intentional.";
            }
            return null;
        }

        private static void AddToHistory(string code, object result, double elapsedMs, bool safetyChecks, string compiler = "auto")
        {
            string preview;
            if (result is SuccessResponse sr)
                preview = sr.Data?.ToString() ?? sr.Message;
            else if (result is ErrorResponse er)
                preview = er.Error;
            else
                preview = result?.ToString() ?? "null";

            if (preview != null && preview.Length > 200)
                preview = preview.Substring(0, 200) + "...";

            _history.Add(new HistoryEntry
            {
                code = code,
                success = result is SuccessResponse,
                resultPreview = preview,
                elapsedMs = Math.Round(elapsedMs, 1),
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                safetyChecksEnabled = safetyChecks,
                compiler = compiler,
            });

            while (_history.Count > MaxHistoryEntries)
                _history.RemoveAt(0);
        }

        private static object SerializeResult(object result)
        {
            if (result == null) return null;

            try
            {
                if (_serializeValueMethod != null)
                    return _serializeValueMethod.Invoke(null, new[] { result });

                if (result is GameObject gameObject)
                    return GameObjectSerializer.GetGameObjectData(gameObject);
                if (result is Component component)
                    return GameObjectSerializer.GetComponentData(component, includeNonPublicSerializedFields: false);

                var serializer = _gameObjectOutputSerializerField?.GetValue(null) as Newtonsoft.Json.JsonSerializer;
                return serializer != null
                    ? JToken.FromObject(result, serializer)
                    : result.ToString();
            }
            catch
            {
                return result.ToString();
            }
        }

        private sealed class CompiledSnippet
        {
            public CompiledSnippet(MethodInfo method, string compiler)
            {
                Method = method;
                Compiler = compiler;
            }

            public MethodInfo Method { get; }

            public string Compiler { get; }
        }

        private class HistoryEntry
        {
            public string code;
            public bool success;
            public string resultPreview;
            public double elapsedMs;
            public string timestamp;
            public bool safetyChecksEnabled;
            public string compiler;
        }
    }

    /// <summary>
    /// Roslyn compiler backend accessed entirely via reflection.
    /// No compile-time dependency on Microsoft.CodeAnalysis — works only if the package is installed.
    /// </summary>
    internal static class RoslynCompiler
    {
        private static bool? _isAvailable;
        private static Type _syntaxTreeType;
        private static Type _compilationType;
        private static Type _compilationOptionsType;
        private static Type _parseOptionsType;
        private static Type _metadataReferenceType;
        private static Type _outputKindEnum;
        private static Type _languageVersionEnum;
        private static MethodInfo _parseText;
        private static MethodInfo _createCompilation;
        private static MethodInfo _createFromFile;
        private static MethodInfo _emit;
        private static object _parseOptions;
        private static object _compilationOptions;
        private static System.Collections.IList _cachedMetadataReferences;
        private static string _cachedMetadataReferenceFingerprint;

        internal static int CachedMetadataReferenceCount => _cachedMetadataReferences?.Count ?? 0;

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable == null)
                    _isAvailable = Initialize();
                return _isAvailable.Value;
            }
        }

        public static void ResetCache()
        {
            _isAvailable = null;
            _cachedMetadataReferences = null;
            _cachedMetadataReferenceFingerprint = null;
        }

        private static bool Initialize()
        {
            try
            {
                _syntaxTreeType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree, Microsoft.CodeAnalysis.CSharp");
                _compilationType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation, Microsoft.CodeAnalysis.CSharp");
                _compilationOptionsType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions, Microsoft.CodeAnalysis.CSharp");
                _parseOptionsType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions, Microsoft.CodeAnalysis.CSharp");
                _metadataReferenceType = Type.GetType("Microsoft.CodeAnalysis.MetadataReference, Microsoft.CodeAnalysis");
                _outputKindEnum = Type.GetType("Microsoft.CodeAnalysis.OutputKind, Microsoft.CodeAnalysis");
                _languageVersionEnum = Type.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion, Microsoft.CodeAnalysis.CSharp");

                if (_syntaxTreeType == null || _compilationType == null || _compilationOptionsType == null ||
                    _parseOptionsType == null || _metadataReferenceType == null || _outputKindEnum == null ||
                    _languageVersionEnum == null)
                    return false;

                // CSharpSyntaxTree.ParseText(string, CSharpParseOptions, string, Encoding, CancellationToken)
                var syntaxTreeBase = Type.GetType("Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis");
                _parseText = _syntaxTreeType.GetMethod("ParseText", new[] { typeof(string), _parseOptionsType, typeof(string), typeof(Encoding), typeof(System.Threading.CancellationToken) });
                if (_parseText == null)
                    return false;

                // CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, CSharpCompilationOptions)
                var metadataRefBase = _metadataReferenceType;
                var syntaxTreeEnumerable = typeof(IEnumerable<>).MakeGenericType(syntaxTreeBase);
                var metadataRefEnumerable = typeof(IEnumerable<>).MakeGenericType(metadataRefBase);
                _createCompilation = _compilationType.GetMethod("Create", new[] { typeof(string), syntaxTreeEnumerable, metadataRefEnumerable, _compilationOptionsType });
                if (_createCompilation == null)
                    return false;

                // MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)
                _createFromFile = _metadataReferenceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateFromFile");
                if (_createFromFile == null)
                    return false;

                // Emit has no single-param overload; the simplest is
                // Emit(Stream, Stream, Stream, Stream, IEnumerable<ResourceDescription>, EmitOptions, CancellationToken)
                var compilationBase = Type.GetType("Microsoft.CodeAnalysis.Compilation, Microsoft.CodeAnalysis");
                if (compilationBase == null) return false;
                _emit = compilationBase.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Emit")
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();
                if (_emit == null)
                    return false;

                // Build CSharpParseOptions — constructor has optional params, use reflection
                var latestValue = Enum.Parse(_languageVersionEnum, "Latest");
                var parseOptionsCtor = _parseOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0];
                var parseCtorParams = parseOptionsCtor.GetParameters();
                var parseArgs = new object[parseCtorParams.Length];
                for (int i = 0; i < parseCtorParams.Length; i++)
                {
                    if (parseCtorParams[i].Name == "languageVersion")
                        parseArgs[i] = latestValue;
                    else if (parseCtorParams[i].HasDefaultValue)
                        parseArgs[i] = parseCtorParams[i].DefaultValue;
                    else
                        parseArgs[i] = null;
                }
                _parseOptions = parseOptionsCtor.Invoke(parseArgs);

                // Build CSharpCompilationOptions — use the first constructor (has most defaults)
                var dllKind = Enum.Parse(_outputKindEnum, "DynamicallyLinkedLibrary");
                var compOptionsCtor = _compilationOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0];
                var compCtorParams = compOptionsCtor.GetParameters();
                var compArgs = new object[compCtorParams.Length];
                for (int i = 0; i < compCtorParams.Length; i++)
                {
                    if (compCtorParams[i].Name == "outputKind")
                        compArgs[i] = dllKind;
                    else if (compCtorParams[i].HasDefaultValue)
                        compArgs[i] = compCtorParams[i].DefaultValue;
                    else
                        compArgs[i] = null;
                }
                _compilationOptions = compOptionsCtor.Invoke(compArgs);

                return true;
            }
            catch (Exception e)
            {
                McpLog.Warn($"[ExecuteCode] Roslyn initialization failed: {e.Message}");
                return false;
            }
        }

        private static System.Collections.IList GetMetadataReferences(string[] assemblyPaths)
        {
            string fingerprint = string.Join("\n", assemblyPaths);
            if (_cachedMetadataReferences != null &&
                string.Equals(
                    _cachedMetadataReferenceFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                return _cachedMetadataReferences;
            }

            Type listType = typeof(List<>).MakeGenericType(_metadataReferenceType);
            System.Collections.IList references =
                (System.Collections.IList)Activator.CreateInstance(listType);
            ParameterInfo[] createFromFileParameters = _createFromFile.GetParameters();

            foreach (string path in assemblyPaths)
            {
                try
                {
                    object[] createFromFileArguments = new object[createFromFileParameters.Length];
                    createFromFileArguments[0] = path;
                    for (int i = 1; i < createFromFileParameters.Length; i++)
                    {
                        createFromFileArguments[i] = createFromFileParameters[i].HasDefaultValue
                            ? createFromFileParameters[i].DefaultValue
                            : null;
                    }

                    object metadataReference = _createFromFile.Invoke(null, createFromFileArguments);
                    references.Add(metadataReference);
                }
                catch
                {
                    // Skip assemblies that can't be loaded as metadata
                }
            }

            _cachedMetadataReferenceFingerprint = fingerprint;
            _cachedMetadataReferences = references;
            return references;
        }

        public static Assembly Compile(string source, string[] assemblyPaths, out List<string> errors)
        {
            errors = new List<string>();

            try
            {
                // Parse source
                var syntaxTree = _parseText.Invoke(null, new object[] { source, _parseOptions, null, null, default(System.Threading.CancellationToken) });

                // Build metadata references
                System.Collections.IList refs = GetMetadataReferences(assemblyPaths);

                // Build syntax tree array
                var syntaxTreeBase = Type.GetType("Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis");
                var treeArray = Array.CreateInstance(syntaxTreeBase, 1);
                treeArray.SetValue(syntaxTree, 0);

                // Create compilation
                var compilation = _createCompilation.Invoke(null, new object[] { "MCPDynamic", treeArray, refs, _compilationOptions });

                // Emit to memory
                using (var ms = new MemoryStream())
                {
                    // Build args for the Emit overload (fill non-stream params with defaults)
                    var emitParams = _emit.GetParameters();
                    var emitArgs = new object[emitParams.Length];
                    emitArgs[0] = ms; // peStream
                    for (int i = 1; i < emitParams.Length; i++)
                    {
                        if (emitParams[i].HasDefaultValue)
                            emitArgs[i] = emitParams[i].DefaultValue;
                        else
                            emitArgs[i] = null;
                    }
                    var emitResult = _emit.Invoke(compilation, emitArgs);

                    // Check emitResult.Success
                    var successProp = emitResult.GetType().GetProperty("Success");
                    bool success = (bool)successProp.GetValue(emitResult);

                    if (!success)
                    {
                        // Read emitResult.Diagnostics
                        var diagProp = emitResult.GetType().GetProperty("Diagnostics");
                        var diagnostics = (System.Collections.IEnumerable)diagProp.GetValue(emitResult);
                        var severityError = Enum.Parse(Type.GetType("Microsoft.CodeAnalysis.DiagnosticSeverity, Microsoft.CodeAnalysis"), "Error");

                        foreach (var diag in diagnostics)
                        {
                            var sevProp = diag.GetType().GetProperty("Severity");
                            var severity = sevProp.GetValue(diag);
                            if (!severity.Equals(severityError)) continue;

                            var locProp = diag.GetType().GetProperty("Location");
                            var loc = locProp.GetValue(diag);
                            var spanProp = loc.GetType().GetMethod("GetLineSpan");
                            var lineSpan = spanProp.Invoke(loc, null);
                            var startProp = lineSpan.GetType().GetProperty("StartLinePosition");
                            var startPos = startProp.GetValue(lineSpan);
                            var lineProp = startPos.GetType().GetProperty("Line");
                            int line = (int)lineProp.GetValue(startPos);

                            var msgProp = diag.GetType().GetMethod("GetMessage", new[] { typeof(System.Globalization.CultureInfo) });
                            string msg = (string)msgProp.Invoke(diag, new object[] { null });

                            int userLine = Math.Max(1, line + 1 - ExecuteCode.WrapperLineOffset);
                            errors.Add($"Line {userLine}: {msg}");
                        }
                        return null;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    return Assembly.Load(ms.ToArray());
                }
            }
            catch (Exception e)
            {
                // Walk to the deepest cause: TargetInvocationException (and friends) wrap the real
                // failure inside .InnerException, and reporting only e.Message hides everything
                // useful (e.g. a missing transitive dep manifests as the generic "Exception has been
                // thrown by the target of an invocation.").
                Exception root = e;
                while (root.InnerException != null) root = root.InnerException;
                errors.Add($"Roslyn compilation error: {root.GetType().Name}: {root.Message}");
                return null;
            }
        }
    }
}
