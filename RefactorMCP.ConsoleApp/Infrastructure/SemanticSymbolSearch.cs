using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;

internal sealed record SemanticSymbolMatch(Document Document, Location Location, bool IsDeclaration);

internal static class SemanticSymbolSearch
{
    internal static Task<IReadOnlyList<SemanticSymbolMatch>> FindMatchesAsync(
        Solution solution,
        IEnumerable<ISymbol> relatedSymbols,
        string identifierName,
        CancellationToken cancellationToken) =>
        FindMatchesAsync(
            solution,
            SymbolIdentity.CreateDeclarationKeys(relatedSymbols),
            identifierName,
            cancellationToken);

    internal static async Task<IReadOnlyList<SemanticSymbolMatch>> FindMatchesAsync(
        Solution solution,
        HashSet<string> declarationKeys,
        string identifierName,
        CancellationToken cancellationToken)
    {
        var matches = new List<SemanticSymbolMatch>();
        var seenMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsSupportedDocument(document))
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || semanticModel == null)
                {
                    continue;
                }

                foreach (var token in root.DescendantTokens().Where(IsIdentifierToken))
                {
                    if (!string.Equals(token.ValueText, identifierName, StringComparison.Ordinal) &&
                        !string.Equals(token.Text, identifierName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var symbol = ResolveMatchedSymbol(semanticModel, token.Parent, cancellationToken);
                    if (symbol == null || !string.Equals(symbol.Name, identifierName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var canonicalSymbol = await SymbolClosure.CanonicalizeAsync(symbol, solution, cancellationToken);
                    if (!SymbolIdentity.GetDeclarationKeys(canonicalSymbol).Any(declarationKeys.Contains))
                    {
                        continue;
                    }

                    var location = token.GetLocation();
                    if (!location.IsInSource || location.SourceTree == null)
                    {
                        continue;
                    }

                    var isDeclaration = declarationKeys.Contains(SymbolIdentity.CreateDeclarationKey(location));
                    var matchKey = $"{SymbolIdentity.CreateDeclarationKey(location)}:{isDeclaration}";
                    if (!seenMatches.Add(matchKey))
                    {
                        continue;
                    }

                    matches.Add(new SemanticSymbolMatch(document, location, isDeclaration));
                }
            }
        }

        DiagnosticTrace.Log(
            "SemanticSymbolSearch",
            "Scanned semantic matches",
            new
            {
                identifierName,
                declarationKeyCount = declarationKeys.Count,
                totalMatches = matches.Count,
                declarationMatches = matches.Count(match => match.IsDeclaration),
                referenceMatches = matches.Count(match => !match.IsDeclaration)
            });

        return matches;
    }

    internal static async Task<Solution> ApplyRenameFallbackAsync(
        Solution lookupSolution,
        Solution updateSolution,
        HashSet<string> declarationKeys,
        string oldName,
        string newName,
        CancellationToken cancellationToken)
    {
        var matches = await FindMatchesAsync(lookupSolution, declarationKeys, oldName, cancellationToken);
        var updatedSolution = updateSolution;

        foreach (var matchesByDocument in matches.GroupBy(match => match.Document.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lookupDocument = lookupSolution.GetDocument(matchesByDocument.Key);
            var document = updatedSolution.GetDocument(matchesByDocument.Key);
            if (lookupDocument == null || document == null)
            {
                continue;
            }

            var lookupText = await lookupDocument.GetTextAsync(cancellationToken);
            var sourceText = await document.GetTextAsync(cancellationToken);
            var seenSpans = new HashSet<TextSpan>();
            var changes = matchesByDocument
                .OrderByDescending(match => match.Location.SourceSpan.Start)
                .Select(match => TryCreateRenameChange(
                    lookupText,
                    sourceText,
                    match.Location.SourceSpan,
                    oldName,
                    newName))
                .Where(change => change.HasValue && seenSpans.Add(change.Value.Span))
                .Select(change => change!.Value)
                .ToArray();
            if (changes.Length == 0)
            {
                continue;
            }

            var updatedText = sourceText.WithChanges(changes);
            updatedSolution = updatedSolution.WithDocumentText(
                document.Id,
                updatedText,
                PreservationMode.PreserveIdentity);
        }

        DiagnosticTrace.Log(
            "SemanticSymbolSearch",
            "Applied semantic rename fallback",
            new
            {
                oldName,
                newName,
                matchCount = matches.Count
            });

        return updatedSolution;
    }

    private static TextChange? TryCreateRenameChange(
        SourceText lookupText,
        SourceText updateText,
        TextSpan originalSpan,
        string oldName,
        string newName)
    {
        var mappedSpan = TryMapRenameSpan(lookupText, updateText, originalSpan, oldName);
        return mappedSpan.HasValue
            ? new TextChange(mappedSpan.Value, newName)
            : null;
    }

    private static TextSpan? TryMapRenameSpan(
        SourceText lookupText,
        SourceText updateText,
        TextSpan originalSpan,
        string expectedText)
    {
        if (TryGetExactIdentifierSpan(updateText, originalSpan.Start, expectedText, out var exactSpan))
        {
            return exactSpan;
        }

        var lookupLine = lookupText.Lines.GetLineFromPosition(originalSpan.Start);
        if (lookupLine.LineNumber >= updateText.Lines.Count)
        {
            return null;
        }

        var updateLine = updateText.Lines[lookupLine.LineNumber];
        var originalColumn = originalSpan.Start - lookupLine.Start;
        return TryFindNearestIdentifierSpanOnLine(updateText, updateLine, expectedText, originalColumn, out var mappedSpan)
            ? mappedSpan
            : null;
    }

    private static bool TryGetExactIdentifierSpan(
        SourceText text,
        int start,
        string expectedText,
        out TextSpan span)
    {
        span = default;
        if (start < 0 || start + expectedText.Length > text.Length)
        {
            return false;
        }

        var candidateSpan = new TextSpan(start, expectedText.Length);
        if (!string.Equals(text.ToString(candidateSpan), expectedText, StringComparison.Ordinal) ||
            !IsIdentifierBoundary(text, candidateSpan.Start - 1) ||
            !IsIdentifierBoundary(text, candidateSpan.End))
        {
            return false;
        }

        span = candidateSpan;
        return true;
    }

    private static bool TryFindNearestIdentifierSpanOnLine(
        SourceText text,
        TextLine line,
        string expectedText,
        int targetColumn,
        out TextSpan span)
    {
        span = default;
        var lineText = text.ToString(line.Span);
        var bestSpan = default(TextSpan);
        var bestDistance = int.MaxValue;

        for (var index = lineText.IndexOf(expectedText, StringComparison.Ordinal);
             index >= 0;
             index = lineText.IndexOf(expectedText, index + 1, StringComparison.Ordinal))
        {
            var absoluteStart = line.Start + index;
            if (!TryGetExactIdentifierSpan(text, absoluteStart, expectedText, out var candidateSpan))
            {
                continue;
            }

            var distance = Math.Abs(index - targetColumn);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestSpan = candidateSpan;
            bestDistance = distance;
        }

        if (bestDistance == int.MaxValue)
        {
            return false;
        }

        span = bestSpan;
        return true;
    }

    private static bool IsIdentifierBoundary(SourceText text, int index)
    {
        if (index < 0 || index >= text.Length)
        {
            return true;
        }

        return !SyntaxFacts.IsIdentifierPartCharacter(text[index]);
    }

    private static bool IsSupportedDocument(Document document)
    {
        if (!string.Equals(Path.GetExtension(document.FilePath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (document.FilePath == null)
        {
            return false;
        }

        var normalizedPath = RefactoringHelpers.NormalizePathForComparison(document.FilePath);
        return !normalizedPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               !normalizedPath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdentifierToken(SyntaxToken token) =>
        token.RawKind == (int)SyntaxKind.IdentifierToken;

    private static ISymbol? ResolveMatchedSymbol(
        SemanticModel semanticModel,
        SyntaxNode? node,
        CancellationToken cancellationToken)
    {
        return node == null
            ? null
            : SymbolResolution.GetSymbolFromNode(semanticModel, node);
    }

    private static ISymbol? SelectBestCandidateSymbol(IEnumerable<ISymbol> candidateSymbols) =>
        candidateSymbols
            .OrderBy(GetCandidatePriority)
            .ThenBy(candidate => candidate.ToDisplayString(), StringComparer.Ordinal)
            .FirstOrDefault();

    private static int GetCandidatePriority(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol => 0,
            IMethodSymbol { MethodKind: MethodKind.Ordinary } => 1,
            IPropertySymbol => 2,
            IFieldSymbol => 3,
            IEventSymbol => 4,
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => 5,
            IParameterSymbol => 6,
            ILocalSymbol => 7,
            _ => 100
        };
}
