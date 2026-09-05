using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Resources.Tests;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Starts a Unity Test Runner run asynchronously and returns a job id immediately.
    /// Use get_test_job(job_id) to poll status/results.
    /// </summary>
    [McpForUnityTool("run_tests", AutoRegister = false, Group = "testing")]
    public static class RunTests
    {
        public static Task<object> HandleCommand(JObject @params)
        {
            try
            {
                // Check for clear_stuck action first
                if (ParamCoercion.CoerceBool(@params?["clear_stuck"], false))
                {
                    bool wasCleared = TestJobManager.ClearStuckJob();
                    return Task.FromResult<object>(new SuccessResponse(
                        wasCleared ? "Stuck job cleared." : "No running job to clear.",
                        new { cleared = wasCleared }
                    ));
                }

                string modeStr = @params?["mode"]?.ToString();
                if (string.IsNullOrWhiteSpace(modeStr))
                {
                    modeStr = "EditMode";
                }

                if (!ModeParser.TryParse(modeStr, out var parsedMode, out var parseError))
                {
                    return Task.FromResult<object>(new ErrorResponse(parseError));
                }

                var p = new ToolParams(@params);
                bool includeDetails = p.GetBool("includeDetails");
                bool includeFailedTests = p.GetBool("includeFailedTests", true);
                int minimumExpectedTests = p.GetInt("minimumTests", 1) ?? 1;
                if (minimumExpectedTests < 1)
                {
                    return Task.FromResult<object>(new ErrorResponse("minimum_tests must be at least 1."));
                }

                string fidelityValue = p.Get("fidelity", "native");
                TestExecutionFidelity fidelity;
                if (string.Equals(fidelityValue, "native", StringComparison.OrdinalIgnoreCase))
                {
                    fidelity = TestExecutionFidelity.Native;
                }
                else if (string.Equals(fidelityValue, "bridge_preserving", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(fidelityValue, "bridge-preserving", StringComparison.OrdinalIgnoreCase))
                {
                    fidelity = TestExecutionFidelity.BridgePreserving;
                }
                else
                {
                    return Task.FromResult<object>(new ErrorResponse(
                        "fidelity must be either 'native' or 'bridge_preserving'."));
                }

                TestFilterOptions filterOptions = GetFilterOptions(@params);
                long requestedInitTimeoutMs = p.GetInt("initTimeout") ?? 0;
                if (!TestJobManager.TryResolveInitializationTimeout(
                        parsedMode.Value,
                        requestedInitTimeoutMs,
                        out long effectiveInitTimeoutMs,
                        out string initTimeoutError))
                {
                    return Task.FromResult<object>(ErrorResponse.Structured(
                        "invalid_init_timeout",
                        initTimeoutError,
                        new
                        {
                            received_ms = requestedInitTimeoutMs,
                            minimum_ms = TestJobManager.MinimumInitializationTimeoutMs,
                            maximum_ms = TestJobManager.MaximumInitializationTimeoutMs,
                            editmode_default_ms = TestJobManager.DefaultEditModeInitializationTimeoutMs,
                            playmode_default_ms = TestJobManager.DefaultPlayModeInitializationTimeoutMs
                        },
                        "Use 60000 for 60 seconds or omit init_timeout_ms for the mode-specific default."));
                }
                TestJobOptions options = new()
                {
                    IncludeDetails = includeDetails,
                    IncludeFailedTests = includeFailedTests,
                    MinimumExpectedTests = minimumExpectedTests,
                    ExpectedTestNames = p.GetStringArray("expectedTests"),
                    FailOnSkipped = p.GetBool("failOnSkipped", true),
                    Execution = new TestExecutionOptions
                    {
                        Fidelity = fidelity,
                        AllowSceneSave = p.GetBool("allowSceneSave", false)
                    }
                };

                bool isCompiling = EditorStateCache.GetActualIsCompiling();
                bool isUpdating = EditorApplication.isUpdating;
                if (isCompiling || isUpdating)
                {
                    return Task.FromResult<object>(ErrorResponse.Structured(
                        "editor_busy",
                        "Cannot start a Unity test job while scripts are compiling or assets are updating.",
                        new { is_compiling = isCompiling, is_updating = isUpdating },
                        "Wait for the Editor to become idle, then call run_tests directly again."));
                }

                if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    return Task.FromResult<object>(ErrorResponse.Structured(
                        "play_mode_transition",
                        "Cannot start a Unity test job while the Editor is in or entering Play Mode.",
                        hint: "Stop Play Mode, wait for the Editor to become idle, then retry."));
                }

                string jobId = TestJobManager.StartJob(parsedMode.Value, filterOptions, effectiveInitTimeoutMs, options);

                return Task.FromResult<object>(new SuccessResponse("Test job started.", new
                {
                    job_id = jobId,
                    status = "running",
                    mode = parsedMode.Value.ToString(),
                    effective_init_timeout_ms = effectiveInitTimeoutMs,
                    include_details = includeDetails,
                    include_failed_tests = includeFailedTests,
                    fidelity = fidelity == TestExecutionFidelity.Native ? "native" : "bridge_preserving",
                    allow_scene_save = options.Execution.AllowSceneSave,
                    coverage = new
                    {
                        minimum_expected_tests = minimumExpectedTests,
                        expected_test_names = options.ExpectedTestNames,
                        fail_on_skipped = options.FailOnSkipped
                    }
                }));
            }
            catch (Exception ex)
            {
                // Normalize the already-running case to a stable error token.
                if (ex.Message != null && ex.Message.IndexOf("already in progress", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Task.FromResult<object>(new ErrorResponse("tests_running", new { reason = "tests_running", retry_after_ms = 5000 }));
                }
                return Task.FromResult<object>(new ErrorResponse($"Failed to start test job: {ex.Message}"));
            }
        }

        private static TestFilterOptions GetFilterOptions(JObject @params)
        {
            if (@params == null)
            {
                return null;
            }

            var p = new ToolParams(@params);
            var testNames = p.GetStringArray("testNames");
            var groupNames = p.GetStringArray("groupNames");
            var categoryNames = p.GetStringArray("categoryNames");
            var assemblyNames = p.GetStringArray("assemblyNames");

            if (testNames == null && groupNames == null && categoryNames == null && assemblyNames == null)
            {
                return null;
            }

            return new TestFilterOptions
            {
                TestNames = testNames,
                GroupNames = groupNames,
                CategoryNames = categoryNames,
                AssemblyNames = assemblyNames
            };
        }
    }
}
