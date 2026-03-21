using ModelContextProtocol.Server;
using ModelContextProtocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.IO;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using Microsoft.CodeAnalysis.Host.Mef;
using System.Collections.Generic;
using System.Text.RegularExpressions;



internal static class RefactoringHelpers
{
    internal const string SolutionPathDescription = "Absolute path to the solution file (.sln or .slnx)";
    private static readonly string[] SupportedSolutionExtensions = [".slnx", ".sln"];

    // MemoryCache is thread-safe and Solution objects from Roslyn are immutable.
    // This allows us to store and access Solution instances across threads
    // without additional locking or synchronization.
    internal static MemoryCache SolutionCache = new(new MemoryCacheOptions());
    internal static MemoryCache SyntaxTreeCache = new(new MemoryCacheOptions());
    internal static MemoryCache ModelCache = new(new MemoryCacheOptions());
    internal static MemoryCache RazorSolutionContextCache = new(new MemoryCacheOptions());

    internal static void ClearAllCaches()
    {
        SolutionCache.Dispose();
        SolutionCache = new MemoryCache(new MemoryCacheOptions());
        SyntaxTreeCache.Dispose();
        SyntaxTreeCache = new MemoryCache(new MemoryCacheOptions());
        ModelCache.Dispose();
        ModelCache = new MemoryCache(new MemoryCacheOptions());
        RazorSolutionContextCache.Dispose();
        RazorSolutionContextCache = new MemoryCache(new MemoryCacheOptions());
    }

    private static readonly Lazy<AdhocWorkspace> _workspace =
        new(() => new AdhocWorkspace());

    private static bool _msbuildRegistered;
    private static readonly object _msbuildLock = new();

    internal static AdhocWorkspace SharedWorkspace => _workspace.Value;

    private static void EnsureMsBuildRegistered()
    {
        if (_msbuildRegistered) return;
        lock (_msbuildLock)
        {
            if (_msbuildRegistered) return;
            MSBuildLocator.RegisterDefaults();
            _msbuildRegistered = true;
        }
    }

    internal static MSBuildWorkspace CreateWorkspace()
    {
        EnsureMsBuildRegistered();
        var host = MefHostServices.Create(MSBuildMefHostServices.DefaultAssemblies);
        var workspace = MSBuildWorkspace.Create(host);
        workspace.WorkspaceFailed += (_, e) =>
            Console.Error.WriteLine(e.Diagnostic.Message);
        return workspace;
    }

    internal static bool TryResolveSolutionPath(string solutionPath, out string resolvedSolutionPath)
    {
        resolvedSolutionPath = string.Empty;
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(solutionPath);
        if (File.Exists(fullPath))
        {
            resolvedSolutionPath = fullPath;
            return true;
        }

        if (!Path.HasExtension(fullPath))
        {
            foreach (var extension in SupportedSolutionExtensions)
            {
                var candidate = fullPath + extension;
                if (File.Exists(candidate))
                {
                    resolvedSolutionPath = candidate;
                    return true;
                }
            }
        }
        else if (string.Equals(Path.GetExtension(fullPath), ".sln", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetExtension(fullPath), ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var alternateExtension = string.Equals(Path.GetExtension(fullPath), ".sln", StringComparison.OrdinalIgnoreCase)
                ? ".slnx"
                : ".sln";
            var alternatePath = Path.ChangeExtension(fullPath, alternateExtension);
            if (File.Exists(alternatePath))
            {
                resolvedSolutionPath = alternatePath;
                return true;
            }
        }

        if (Directory.Exists(fullPath))
        {
            foreach (var extension in SupportedSolutionExtensions)
            {
                var candidate = Directory.EnumerateFiles(fullPath, $"*{extension}", SearchOption.TopDirectoryOnly)
                    .OrderBy(Path.GetFileName)
                    .FirstOrDefault();
                if (candidate != null)
                {
                    resolvedSolutionPath = Path.GetFullPath(candidate);
                    return true;
                }
            }
        }

        return false;
    }

    internal static string ResolveSolutionPath(string solutionPath)
    {
        if (TryResolveSolutionPath(solutionPath, out var resolvedSolutionPath))
        {
            return resolvedSolutionPath;
        }

        throw new McpException($"Error: Solution file not found at {solutionPath}");
    }

    internal static async Task<Solution> GetOrLoadSolution(
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        solutionPath = ResolveSolutionPath(solutionPath);

        if (SolutionCache.TryGetValue(solutionPath, out Solution? cachedSolution))
        {
            Directory.SetCurrentDirectory(Path.GetDirectoryName(solutionPath)!);
            return cachedSolution!;
        }
        using var workspace = CreateWorkspace();
        var solution = await workspace.OpenSolutionAsync(solutionPath, progress: null, cancellationToken);
        SolutionCache.Set(solutionPath, solution);
        Directory.SetCurrentDirectory(Path.GetDirectoryName(solutionPath)!);
        return solution;
    }

    // Solutions are immutable, so replacing the cached instance is safe even
    // when accessed concurrently by multiple threads.
    internal static void UpdateSolutionCache(Document updatedDocument)
    {
        var solutionPath = updatedDocument.Project.Solution.FilePath;
        if (!string.IsNullOrEmpty(solutionPath))
        {
            SolutionCache.Set(solutionPath!, updatedDocument.Project.Solution);
            RazorSolutionContextCache.Remove(solutionPath!);
            if (!string.IsNullOrEmpty(updatedDocument.FilePath))
            {
                _ = MetricsProvider.RefreshFileMetrics(solutionPath!, updatedDocument.FilePath!);
            }
        }
    }

    internal static async Task<RazorSolutionContext> GetOrLoadRazorSolutionContext(
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        solutionPath = ResolveSolutionPath(solutionPath);
        if (RazorSolutionContextCache.TryGetValue(solutionPath, out RazorSolutionContext? cachedContext))
            return cachedContext!;

        var solution = await GetOrLoadSolution(solutionPath, cancellationToken);
        var context = await RazorSolutionContext.CreateAsync(solution, cancellationToken);
        RazorSolutionContextCache.Set(solutionPath, context);
        return context;
    }

    internal static void InvalidateSolutionCaches(string solutionPath)
    {
        solutionPath = ResolveSolutionPath(solutionPath);
        SolutionCache.Remove(solutionPath);
        RazorSolutionContextCache.Remove(solutionPath);
    }

    internal static Document? GetDocumentByPath(Solution solution, string filePath)
    {
        var normalizedPath = NormalizePathForComparison(filePath);
        return solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => PathEquals(d.FilePath, normalizedPath));
    }

    internal static string NormalizePathForComparison(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Trim().Replace('\\', '/');
        var wslMatch = Regex.Match(normalized, "^/mnt/([a-zA-Z])/(.+)$");
        if (wslMatch.Success)
        {
            normalized = $"{char.ToLowerInvariant(wslMatch.Groups[1].Value[0])}:/{wslMatch.Groups[2].Value}";
        }
        else
        {
            var windowsMatch = Regex.Match(normalized, "^([a-zA-Z]):/(.+)$");
            if (windowsMatch.Success)
                normalized = $"{char.ToLowerInvariant(windowsMatch.Groups[1].Value[0])}:/{windowsMatch.Groups[2].Value}";
        }

        return normalized.TrimEnd('/').ToLowerInvariant();
    }

    internal static bool PathEquals(string? left, string? right) =>
        string.Equals(
            NormalizePathForComparison(left),
            NormalizePathForComparison(right),
            StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseRange(string range, out int startLine, out int startColumn, out int endLine, out int endColumn)
    {
        startLine = startColumn = endLine = endColumn = 0;
        var parts = range.Split('-');
        if (parts.Length != 2) return false;
        var startParts = parts[0].Split(':');
        var endParts = parts[1].Split(':');
        if (startParts.Length != 2 || endParts.Length != 2) return false;
        return int.TryParse(startParts[0], out startLine) &&
               int.TryParse(startParts[1], out startColumn) &&
               int.TryParse(endParts[0], out endLine) &&
               int.TryParse(endParts[1], out endColumn);
    }

    internal static bool ValidateRange(
        SourceText text,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        out string error)
    {
        error = string.Empty;
        if (startLine <= 0 || startColumn <= 0 || endLine <= 0 || endColumn <= 0)
        {
            error = "Error: Range values must be positive";
            return false;
        }
        if (startLine > endLine || (startLine == endLine && startColumn >= endColumn))
        {
            error = "Error: Range start must precede end";
            return false;
        }
        if (startLine > text.Lines.Count || endLine > text.Lines.Count)
        {
            error = "Error: Range exceeds file length";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Parses a selection range, validates it against the source text, and returns a TextSpan.
    /// Consolidates the common pattern of TryParseRange + ValidateRange + span calculation.
    /// </summary>
    internal static TextSpan ParseSelectionRange(SourceText sourceText, string selectionRange)
    {
        if (!TryParseRange(selectionRange, out var startLine, out var startColumn, out var endLine, out var endColumn))
            throw new McpException("Error: Invalid selection range format. Use 'startLine:startColumn-endLine:endColumn'");

        if (!ValidateRange(sourceText, startLine, startColumn, endLine, endColumn, out var error))
            throw new McpException(error);

        var startPosition = sourceText.Lines[startLine - 1].Start + startColumn - 1;
        var endPosition = sourceText.Lines[endLine - 1].Start + endColumn - 1;
        return TextSpan.FromBounds(startPosition, endPosition);
    }

    /// <summary>
    /// Writes file content with encoding and updates all caches.
    /// Consolidates the common pattern of GetFileEncoding + WriteAllText + UpdateSolutionCache.
    /// </summary>
    internal static async Task WriteAndUpdateCachesAsync(Document document, SyntaxNode newRoot)
    {
        var newDocument = document.WithSyntaxRoot(newRoot);
        var newText = await newDocument.GetTextAsync();
        var (originalText, encoding) = await ReadFileWithEncodingAsync(document.FilePath!);
        var updatedText = PreserveLineEndings(newText.ToString(), originalText);
        await File.WriteAllTextAsync(document.FilePath!, updatedText, encoding);
        UpdateSolutionCache(newDocument);
    }


    internal static async Task<string> ApplySingleFileEdit(
        string filePath,
        Func<string, string> transform,
        string successMessage)
    {
        if (!File.Exists(filePath))
            throw new McpException($"Error: File {filePath} not found (current dir: {Directory.GetCurrentDirectory()})");

        var (sourceText, encoding) = await ReadFileWithEncodingAsync(filePath);
        var newText = transform(sourceText);

        if (newText.StartsWith("Error:"))
            return newText;

        var updatedText = PreserveLineEndings(newText, sourceText);
        await File.WriteAllTextAsync(filePath, updatedText, encoding);
        UpdateFileCaches(filePath, updatedText);
        return successMessage;
    }

    internal static async Task<Document?> FindClassInSolution(
        Solution solution,
        string className,
        params string[]? excludingFilePaths)
    {
        foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
        {
            var docPath = doc.FilePath ?? string.Empty;
            if (excludingFilePaths != null && excludingFilePaths.Any(p => Path.GetFullPath(docPath) == Path.GetFullPath(p)))
                continue;

            var root = await doc.GetSyntaxRootAsync();
            if (root != null && root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Any(c => c.Identifier.Text == className))
            {
                return doc;
            }
        }

        return null;
    }

    internal static async Task<Document?> FindTypeInSolution(
        Solution solution,
        string typeName,
        params string[]? excludingFilePaths)
    {
        foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
        {
            var docPath = doc.FilePath ?? string.Empty;
            if (excludingFilePaths != null && excludingFilePaths.Any(p => Path.GetFullPath(docPath) == Path.GetFullPath(p)))
                continue;

            var root = await doc.GetSyntaxRootAsync();
            if (root != null && root.DescendantNodes().Any(n =>
                    n is BaseTypeDeclarationSyntax bt && bt.Identifier.Text == typeName ||
                    n is EnumDeclarationSyntax en && en.Identifier.Text == typeName ||
                    n is DelegateDeclarationSyntax dd && dd.Identifier.Text == typeName))
            {
                return doc;
            }
        }

        return null;
    }

    internal static void AddDocumentToProject(Project project, string filePath)
    {
        if (project.Documents.Any(d =>
                Path.GetFullPath(d.FilePath ?? "") == Path.GetFullPath(filePath)))
            return;

        var text = SourceText.From(File.ReadAllText(filePath));
        var newDoc = project.AddDocument(Path.GetFileName(filePath), text, filePath: filePath);

        var solutionPath = project.Solution.FilePath;
        if (!string.IsNullOrEmpty(solutionPath))
        {
            SolutionCache.Set(solutionPath!, newDoc.Project.Solution);
        }
    }

    private static CSharpCompilation CreateCompilation(SyntaxTree tree)
    {
        var refs = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        return CSharpCompilation.Create(
            "SingleFile",
            new[] { tree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    internal static async Task<SyntaxTree> GetOrParseSyntaxTreeAsync(string filePath)
    {
        if (SyntaxTreeCache.TryGetValue(filePath, out SyntaxTree? cached))
            return cached!;
        var (text, _) = await ReadFileWithEncodingAsync(filePath);
        var tree = CSharpSyntaxTree.ParseText(text);
        SyntaxTreeCache.Set(filePath, tree);
        return tree;
    }

    internal static async Task<SemanticModel> GetOrCreateSemanticModelAsync(string filePath)
    {
        if (ModelCache.TryGetValue(filePath, out SemanticModel? cached))
            return cached!;
        var tree = await GetOrParseSyntaxTreeAsync(filePath);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        ModelCache.Set(filePath, model);
        return model;
    }

    internal static void UpdateFileCaches(string filePath, string newText)
    {
        var tree = CSharpSyntaxTree.ParseText(newText);
        SyntaxTreeCache.Set(filePath, tree);
        var compilation = CreateCompilation(tree);
        var model = compilation.GetSemanticModel(tree);
        ModelCache.Set(filePath, model);
    }

    internal static void RemoveFileCaches(string filePath)
    {
        SyntaxTreeCache.Remove(filePath);
        ModelCache.Remove(filePath);
    }

    internal static void RenameFileCaches(string oldFilePath, string newFilePath, string newText)
    {
        RemoveFileCaches(oldFilePath);
        RemoveFileCaches(newFilePath);

        if (string.Equals(Path.GetExtension(newFilePath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            UpdateFileCaches(newFilePath, newText);
        }
    }

    internal static async Task<(string Text, Encoding Encoding)> ReadFileWithEncodingAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var detectedEncoding = DetectEncoding(bytes);
        var text = detectedEncoding.Encoding.GetString(
            bytes,
            detectedEncoding.PreambleLength,
            bytes.Length - detectedEncoding.PreambleLength);
        return (text, detectedEncoding.Encoding);
    }

    internal static async Task<Encoding> GetFileEncodingAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        return DetectEncoding(bytes).Encoding;
    }

    private static DetectedEncoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return new DetectedEncoding(new UTF32Encoding(true, true), 4);
            if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return new DetectedEncoding(new UTF32Encoding(false, true), 4);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new DetectedEncoding(new UTF8Encoding(true), 3);
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new DetectedEncoding(new UnicodeEncoding(true, true), 2);
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new DetectedEncoding(new UnicodeEncoding(false, true), 2);
        }
        return new DetectedEncoding(new UTF8Encoding(false), 0);
    }

    internal static async Task WriteFileWithEncodingAsync(
        string filePath,
        string text,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        string originalText;
        if (File.Exists(filePath))
        {
            (originalText, _) = await ReadFileWithEncodingAsync(filePath, cancellationToken);
        }
        else
        {
            originalText = text;
        }

        var updatedText = PreserveLineEndings(text, originalText);
        await File.WriteAllTextAsync(filePath, updatedText, encoding, cancellationToken);
        if (string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            UpdateFileCaches(filePath, updatedText);
        }
    }

    internal static string PreserveLineEndings(string updatedText, string originalText)
    {
        var normalizedUpdatedText = updatedText.Replace("\r\n", "\n");
        return originalText.Contains("\r\n")
            ? normalizedUpdatedText.Replace("\n", "\r\n")
            : normalizedUpdatedText;
    }

    private readonly record struct DetectedEncoding(Encoding Encoding, int PreambleLength);

    internal static async Task<string> RunWithSolutionOrFile(
        string solutionPath,
        string filePath,
        Func<Document, Task<string>> withSolution,
        Func<string, Task<string>> singleFile)
    {
        var solution = await GetOrLoadSolution(solutionPath);
        var document = GetDocumentByPath(solution, filePath);
        if (document != null)
            return await withSolution(document);

        return await singleFile(filePath);
    }
}
