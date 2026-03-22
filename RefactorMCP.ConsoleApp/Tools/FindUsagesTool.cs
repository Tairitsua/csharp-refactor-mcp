using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.IO;

[McpServerToolType]
public static class FindUsagesTool
{
    [McpServerTool, Description("Find usages of a C# symbol across the solution")]
    public static async Task<FindUsagesResult> FindUsages(
        [Description(RefactoringHelpers.SolutionPathDescription)] string solutionPath,
        [Description("Path to the C# file containing the symbol")] string filePath,
        [Description("Name of the symbol to inspect")] string symbolName,
        [Description("Line number of the symbol (1-based, optional)")] int? line = null,
        [Description("Column number of the symbol (1-based, optional)")] int? column = null,
        [Description("Maximum number of reference locations to return")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (maxResults <= 0)
            {
                throw new McpException("Error: maxResults must be greater than zero.");
            }

            solutionPath = RefactoringHelpers.ResolveSolutionPath(solutionPath);
            if (!string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
            {
                throw new McpException(
                    $"Error: FindUsages currently supports only C# source files. '{Path.GetFileName(filePath)}' is not supported.");
            }

            var solution = await RefactoringHelpers.GetOrLoadSolution(solutionPath, cancellationToken);
            var symbolSolution = RefactoringHelpers.CreateAnalyzerSafeSolution(solution);
            var document = RefactoringHelpers.GetDocumentByPath(symbolSolution, filePath);
            if (document == null)
            {
                throw new McpException($"Error: File {filePath} not found in solution");
            }

            var symbol = await SymbolResolution.FindSymbolAsync(document, symbolName, line, column, cancellationToken);
            if (symbol == null)
            {
                throw new McpException($"Error: Symbol '{symbolName}' not found");
            }

            symbol = await SymbolClosure.CanonicalizeAsync(symbol, symbolSolution, cancellationToken);
            DiagnosticTrace.Log(
                "FindUsages",
                "Resolved canonical symbol",
                new
                {
                    filePath,
                    symbolName,
                    line,
                    column,
                    symbol = symbol.ToDisplayString()
                });

            var closure = await SymbolClosure.GetRelatedSymbolsAsync(symbol, symbolSolution, cancellationToken);
            var referencedSymbols = await FindReferencesAcrossClosureAsync(closure, symbolSolution, cancellationToken);
            var sourceTextCache = new Dictionary<string, SourceText>(StringComparer.OrdinalIgnoreCase);
            var declarations = await CollectDeclarationLocationsAsync(
                closure,
                symbolSolution,
                sourceTextCache,
                cancellationToken);
            var references = await CollectReferenceLocationsAsync(
                referencedSymbols,
                symbolSolution,
                sourceTextCache,
                cancellationToken);
            references = await MergeSemanticFallbackReferencesAsync(
                references,
                symbolSolution,
                closure,
                symbolName,
                sourceTextCache,
                cancellationToken);
            DiagnosticTrace.Log(
                "FindUsages",
                "Collected usages",
                new
                {
                    symbol = symbol.ToDisplayString(),
                    closureCount = closure.Count,
                    referencedSymbolCount = referencedSymbols.Count,
                    declarationCount = declarations.Count,
                    referenceCount = references.Count
                });

            var orderedReferences = references
                .OrderBy(location => RefactoringHelpers.NormalizePathForComparison(location.FilePath))
                .ThenBy(location => location.Line)
                .ThenBy(location => location.Column)
                .ToList();

            var returnedReferences = orderedReferences.Take(maxResults).ToArray();

            return new FindUsagesResult
            {
                SymbolName = symbol.Name,
                SymbolKind = symbol.Kind.ToString(),
                DisplayName = symbol.ToDisplayString(),
                ContainingSymbol = symbol.ContainingSymbol?.ToDisplayString(),
                TotalReferenceCount = orderedReferences.Count,
                ReturnedReferenceCount = returnedReferences.Length,
                IsTruncated = orderedReferences.Count > returnedReferences.Length,
                Declarations = declarations,
                References = returnedReferences
            };
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException($"Error finding usages: {ex.Message}", ex);
        }
    }

    private static async Task<IReadOnlyList<ReferencedSymbol>> FindReferencesAcrossClosureAsync(
        IReadOnlyCollection<ISymbol> closure,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var referencedSymbols = new List<ReferencedSymbol>();

        foreach (var closureSymbol in closure)
        {
            referencedSymbols.AddRange(
                await SymbolFinder.FindReferencesAsync(closureSymbol, solution, cancellationToken));
        }

        return referencedSymbols;
    }

    private static async Task<IReadOnlyList<FindUsageLocation>> CollectDeclarationLocationsAsync(
        IEnumerable<ISymbol> closure,
        Solution solution,
        Dictionary<string, SourceText> sourceTextCache,
        CancellationToken cancellationToken)
    {
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declarations = new List<FindUsageLocation>();

        foreach (var symbol in closure)
        {
            foreach (var location in symbol.Locations.Where(location => location.IsInSource))
            {
                var usage = await TryCreateLocationAsync(location, solution, sourceTextCache, cancellationToken);
                if (usage == null || !seenLocations.Add(CreateLocationKey(usage)))
                {
                    continue;
                }

                declarations.Add(usage);
            }
        }

        return declarations
            .OrderBy(location => RefactoringHelpers.NormalizePathForComparison(location.FilePath))
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Column)
            .ToArray();
    }

    private static async Task<IReadOnlyList<FindUsageLocation>> CollectReferenceLocationsAsync(
        IEnumerable<ReferencedSymbol> referencedSymbols,
        Solution solution,
        Dictionary<string, SourceText> sourceTextCache,
        CancellationToken cancellationToken)
    {
        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new List<FindUsageLocation>();
        var syntaxRootCache = new Dictionary<SyntaxTree, SyntaxNode>();

        foreach (var referencedSymbol in referencedSymbols)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                if (await IsDocumentationCommentReferenceAsync(location.Location, syntaxRootCache, cancellationToken))
                {
                    continue;
                }

                var usage = await TryCreateLocationAsync(location, solution, sourceTextCache, cancellationToken);
                if (usage == null || !seenLocations.Add(CreateLocationKey(usage)))
                {
                    continue;
                }

                references.Add(usage);
            }
        }

        return references;
    }

    private static async Task<IReadOnlyList<FindUsageLocation>> MergeSemanticFallbackReferencesAsync(
        IReadOnlyList<FindUsageLocation> references,
        Solution solution,
        IReadOnlyCollection<ISymbol> closure,
        string symbolName,
        Dictionary<string, SourceText> sourceTextCache,
        CancellationToken cancellationToken)
    {
        var seenLocations = new HashSet<string>(
            references.Select(CreateLocationKey),
            StringComparer.OrdinalIgnoreCase);
        var mergedReferences = references.ToList();
        var semanticMatches = await SemanticSymbolSearch.FindMatchesAsync(
            solution,
            closure,
            symbolName,
            cancellationToken);

        foreach (var match in semanticMatches.Where(match => !match.IsDeclaration))
        {
            if (await IsDocumentationCommentReferenceAsync(
                    match.Location,
                    new Dictionary<SyntaxTree, SyntaxNode>(),
                    cancellationToken))
            {
                continue;
            }

            var usage = await TryCreateLocationAsync(
                match.Location,
                solution,
                sourceTextCache,
                cancellationToken);
            if (usage == null || !seenLocations.Add(CreateLocationKey(usage)))
            {
                continue;
            }

            mergedReferences.Add(usage);
        }

        DiagnosticTrace.Log(
            "FindUsages",
            "Merged semantic fallback references",
            new
            {
                symbolName,
                originalReferenceCount = references.Count,
                mergedReferenceCount = mergedReferences.Count
            });

        return mergedReferences;
    }

    private static async Task<bool> IsDocumentationCommentReferenceAsync(
        Location location,
        Dictionary<SyntaxTree, SyntaxNode> syntaxRootCache,
        CancellationToken cancellationToken)
    {
        if (!location.IsInSource || location.SourceTree == null || location.SourceSpan.IsEmpty)
        {
            return false;
        }

        if (!syntaxRootCache.TryGetValue(location.SourceTree, out var root))
        {
            root = await location.SourceTree.GetRootAsync(cancellationToken);
            syntaxRootCache[location.SourceTree] = root;
        }

        var token = root.FindToken(location.SourceSpan.Start, findInsideTrivia: true);
        for (var node = token.Parent; node != null; node = node.Parent)
        {
            if (node.RawKind is (int)SyntaxKind.SingleLineDocumentationCommentTrivia or
                (int)SyntaxKind.MultiLineDocumentationCommentTrivia)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<FindUsageLocation?> TryCreateLocationAsync(
        Location location,
        Solution solution,
        Dictionary<string, SourceText> sourceTextCache,
        CancellationToken cancellationToken)
    {
        if (!TryGetPreferredLineSpan(location, out var filePath, out var lineSpan))
        {
            return null;
        }

        var sourceText = location.SourceTree != null &&
                         RefactoringHelpers.PathEquals(location.SourceTree.FilePath, filePath)
            ? await location.SourceTree.GetTextAsync(cancellationToken)
            : null;
        var lineText = await GetLineTextAsync(
            solution,
            filePath,
            lineSpan.StartLinePosition.Line,
            sourceText,
            sourceTextCache,
            cancellationToken);

        return new FindUsageLocation
        {
            FilePath = filePath,
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            LineText = lineText
        };
    }

    private static async Task<FindUsageLocation?> TryCreateLocationAsync(
        ReferenceLocation location,
        Solution solution,
        Dictionary<string, SourceText> sourceTextCache,
        CancellationToken cancellationToken)
    {
        if (!TryGetPreferredLineSpan(location.Location, out var filePath, out var lineSpan))
        {
            return null;
        }

        var sourceText = !string.IsNullOrWhiteSpace(location.Document.FilePath) &&
                         RefactoringHelpers.PathEquals(location.Document.FilePath, filePath)
            ? await location.Document.GetTextAsync(cancellationToken)
            : null;
        var lineText = await GetLineTextAsync(
            solution,
            filePath,
            lineSpan.StartLinePosition.Line,
            sourceText,
            sourceTextCache,
            cancellationToken);

        return new FindUsageLocation
        {
            FilePath = filePath,
            Line = lineSpan.StartLinePosition.Line + 1,
            Column = lineSpan.StartLinePosition.Character + 1,
            LineText = lineText
        };
    }

    private static bool TryGetPreferredLineSpan(
        Location location,
        out string filePath,
        out FileLinePositionSpan lineSpan)
    {
        var mappedLineSpan = location.GetMappedLineSpan();
        if (mappedLineSpan.IsValid &&
            mappedLineSpan.HasMappedPath &&
            !string.IsNullOrWhiteSpace(mappedLineSpan.Path))
        {
            filePath = mappedLineSpan.Path;
            lineSpan = mappedLineSpan;
            return true;
        }

        lineSpan = location.GetLineSpan();
        filePath = lineSpan.Path;
        if (string.IsNullOrWhiteSpace(filePath) && location.SourceTree != null)
        {
            filePath = location.SourceTree.FilePath;
        }

        return lineSpan.IsValid && !string.IsNullOrWhiteSpace(filePath);
    }

    private static async Task<string> GetLineTextAsync(
        Solution solution,
        string filePath,
        int lineIndex,
        SourceText? sourceText,
        Dictionary<string, SourceText> sourceTextCache,
        CancellationToken cancellationToken)
    {
        if (sourceText != null)
        {
            return GetLineText(sourceText, lineIndex);
        }

        var normalizedFilePath = RefactoringHelpers.NormalizePathForComparison(filePath);
        if (sourceTextCache.TryGetValue(normalizedFilePath, out var cachedSourceText))
        {
            return GetLineText(cachedSourceText, lineIndex);
        }

        var document = RefactoringHelpers.GetDocumentByPath(solution, filePath);
        if (document != null)
        {
            var documentText = await document.GetTextAsync(cancellationToken);
            sourceTextCache[normalizedFilePath] = documentText;
            return GetLineText(documentText, lineIndex);
        }

        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        var (fileContent, _) = await RefactoringHelpers.ReadFileWithEncodingAsync(filePath, cancellationToken);
        var fileText = SourceText.From(fileContent);
        sourceTextCache[normalizedFilePath] = fileText;
        return GetLineText(fileText, lineIndex);
    }

    private static string CreateLocationKey(FindUsageLocation location)
    {
        return $"{RefactoringHelpers.NormalizePathForComparison(location.FilePath)}:{location.Line}:{location.Column}";
    }

    private static string GetLineText(SourceText sourceText, int lineIndex)
    {
        return lineIndex >= 0 && lineIndex < sourceText.Lines.Count
            ? sourceText.Lines[lineIndex].ToString().Trim()
            : string.Empty;
    }
}

public sealed class FindUsagesResult
{
    public string SymbolName { get; init; } = string.Empty;
    public string SymbolKind { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? ContainingSymbol { get; init; }
    public int TotalReferenceCount { get; init; }
    public int ReturnedReferenceCount { get; init; }
    public bool IsTruncated { get; init; }
    public IReadOnlyList<FindUsageLocation> Declarations { get; init; } = [];
    public IReadOnlyList<FindUsageLocation> References { get; init; } = [];
}

public sealed class FindUsageLocation
{
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public int Column { get; init; }
    public string LineText { get; init; } = string.Empty;
}
