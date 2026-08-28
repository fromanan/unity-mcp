using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Poll a previously started async test job by job_id.
    /// </summary>
    [McpForUnityTool("get_test_job", AutoRegister = false, Group = "testing")]
    public static class GetTestJob
    {
        public static object HandleCommand(JObject @params)
        {
            string jobId = @params?["job_id"]?.ToString() ?? @params?["jobId"]?.ToString();
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return new ErrorResponse("Missing required parameter 'job_id'.");
            }

            var p = new ToolParams(@params);
            bool includeDetails = p.GetBool("includeDetails");
            bool includeFailedTests = p.GetBool("includeFailedTests", true);

            var job = TestJobManager.GetJob(jobId);
            if (job == null)
            {
                return new ErrorResponse("Unknown job_id.");
            }

            object payload = TestJobManager.ToSerializable(job, includeDetails, includeFailedTests);
            if (job.Status != TestJobStatus.Running && job.Status != TestJobStatus.Passed)
            {
                return new ErrorResponse("validation_failed", payload);
            }
            return new SuccessResponse("Test job status retrieved.", payload);
        }
    }
}
