using System.IO;

namespace RefactorMCP.Tests.Tools;

public static class TestHelpers
{
    private static readonly string[] SolutionFileNames = ["RefactorMCP.slnx", "RefactorMCP.sln"];

    public static string GetSolutionPath()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            foreach (var solutionFileName in SolutionFileNames)
            {
                var solutionFile = Path.Combine(dir.FullName, solutionFileName);
                if (File.Exists(solutionFile)) return solutionFile;
            }
            dir = dir.Parent;
        }
        return "./RefactorMCP.slnx";
    }

    public static string CreateTestOutputDir(string subfolder)
    {
        var path = Path.Combine(Path.GetDirectoryName(GetSolutionPath())!,
            "RefactorMCP.Tests", "TestOutput", subfolder);
        Directory.CreateDirectory(path);
        return path;
    }
}
