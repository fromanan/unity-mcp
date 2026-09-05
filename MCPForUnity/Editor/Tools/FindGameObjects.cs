using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Tool for searching GameObjects in the scene.
    /// Returns only instance IDs with pagination support.
    /// 
    /// This is a focused search tool that returns lightweight results (IDs only).
    /// For detailed GameObject data, use the unity://scene/gameobject/{id} resource.
    /// </summary>
    [McpForUnityTool("find_gameobjects")]
    public static class FindGameObjects
    {
        private const string AmbiguousComponentTypeCode = "ambiguous_component_type";
        private const string ComponentTypeNotFoundCode = "component_type_not_found";

        /// <summary>
        /// Handles the find_gameobjects command.
        /// </summary>
        /// <param name="params">Command parameters</param>
        /// <returns>Paginated list of instance IDs</returns>
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            var p = new ToolParams(@params);

            // Parse search parameters
            string searchMethod = p.Get("searchMethod", "by_name");

            // Try searchTerm, search_term, or target (for backwards compatibility)
            string searchTerm = p.Get("searchTerm");
            if (string.IsNullOrEmpty(searchTerm))
            {
                searchTerm = p.Get("target");
            }

            if (string.IsNullOrEmpty(searchTerm))
            {
                return new ErrorResponse("'searchTerm' or 'target' parameter is required.");
            }

            // Pagination parameters using standard PaginationRequest
            var pagination = PaginationRequest.FromParams(@params, defaultPageSize: 50);
            pagination.PageSize = Mathf.Clamp(pagination.PageSize, 1, 500);

            // Search options (supports multiple parameter name variants)
            bool includeInactive = p.GetBool("includeInactive", false) ||
                                   p.GetBool("searchInactive", false);

            try
            {
                GameObjectLookup.SearchMethod parsedSearchMethod = GameObjectLookup.ParseSearchMethod(searchMethod);
                if (parsedSearchMethod == GameObjectLookup.SearchMethod.ByComponent &&
                    !UnityTypeResolver.TryResolveDetailed(
                        searchTerm,
                        out System.Type _,
                        out UnityTypeResolver.ResolutionFailure failure,
                        typeof(Component)))
                {
                    string code = failure.Code switch
                    {
                        UnityTypeResolver.AmbiguousTypeCode => AmbiguousComponentTypeCode,
                        UnityTypeResolver.TypeNotFoundCode => ComponentTypeNotFoundCode,
                        _ => failure.Code
                    };
                    McpLog.Warn(
                        $"[FindGameObjects] Component type resolution failed ({code}): {failure.Message}");
                    return ErrorResponse.Structured(
                        code,
                        failure.Message,
                        new
                        {
                            searchMethod = "by_component",
                            searchTerm,
                            candidateCount = failure.CandidateCount,
                            candidates = failure.Candidates
                        },
                        failure.Hint);
                }

                // Get all matching instance IDs
                List<int> allIds = GameObjectLookup.SearchGameObjects(
                    parsedSearchMethod,
                    searchTerm,
                    includeInactive,
                    0);
                
                // Use standard pagination response
                PaginationResponse<int> paginatedResult = PaginationResponse<int>.Create(allIds, pagination);

                return new SuccessResponse("Found GameObjects", new
                {
                    instanceIDs = paginatedResult.Items,
                    pageSize = paginatedResult.PageSize,
                    cursor = paginatedResult.Cursor,
                    nextCursor = paginatedResult.NextCursor,
                    totalCount = paginatedResult.TotalCount,
                    hasMore = paginatedResult.HasMore
                });
            }
            catch (System.Exception ex)
            {
                McpLog.Error($"[FindGameObjects] Error searching GameObjects: {ex.Message}");
                return new ErrorResponse($"Error searching GameObjects: {ex.Message}");
            }
        }
    }
}
