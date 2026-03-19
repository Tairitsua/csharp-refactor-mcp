using ModelContextProtocol.Server;
using Microsoft.Extensions.Caching.Memory;
using System.ComponentModel;
using System.IO;
using System.Threading;

[McpServerToolType]
public static class UnloadSolutionTool
{
    [McpServerTool, Description("Unload a solution and remove it from the cache")]
    public static string UnloadSolution(
        [Description(RefactoringHelpers.SolutionPathDescription)] string solutionPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedSolutionPath = RefactoringHelpers.TryResolveSolutionPath(solutionPath, out var resolvedPath)
            ? resolvedPath
            : Path.GetFullPath(solutionPath);

        if (RefactoringHelpers.SolutionCache.TryGetValue(resolvedSolutionPath, out _))
        {
            RefactoringHelpers.SolutionCache.Remove(resolvedSolutionPath);
            return $"Unloaded solution '{Path.GetFileName(resolvedSolutionPath)}' from cache";
        }

        return $"Solution '{Path.GetFileName(resolvedSolutionPath)}' was not loaded";
    }

    [McpServerTool, Description("Clear all cached solutions")]
    public static string ClearSolutionCache(
        CancellationToken cancellationToken = default)
    {
        RefactoringHelpers.ClearAllCaches();
        return "Cleared all cached solutions";
    }
}
