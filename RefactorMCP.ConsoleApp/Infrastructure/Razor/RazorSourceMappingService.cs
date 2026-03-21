using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal sealed record RazorProjectedEdit(string SourceFilePath, TextSpan Span, string OldText, string NewText);

internal sealed record RazorProjectedChangeSet(ImmutableDictionary<string, ImmutableArray<RazorProjectedEdit>> EditsByFile)
{
    internal bool HasEdits => EditsByFile.Count > 0;
}

internal static class RazorSourceMappingService
{
    internal static async Task<RazorProjectedChangeSet> CreateChangeSetAsync(
        ISymbol symbol,
        Solution solution,
        string oldName,
        string newName,
        CancellationToken cancellationToken)
    {
        var editsByFile = new Dictionary<string, Dictionary<(int Start, int Length), RazorProjectedEdit>>(StringComparer.OrdinalIgnoreCase);
        var sourceTexts = new Dictionary<string, SourceText>(StringComparer.OrdinalIgnoreCase);
        var closure = await SymbolClosure.GetRelatedSymbolsAsync(symbol, solution, cancellationToken);

        foreach (var location in closure.SelectMany(currentSymbol => currentSymbol.Locations))
        {
            await AddMappedEditAsync(location, oldName, newName, editsByFile, sourceTexts, cancellationToken);
        }

        foreach (var closureSymbol in closure)
        {
            var references = await SymbolFinder.FindReferencesAsync(closureSymbol, solution, cancellationToken);
            foreach (var referencedSymbol in references)
            {
                foreach (var location in referencedSymbol.Locations)
                {
                    await AddMappedEditAsync(location.Location, oldName, newName, editsByFile, sourceTexts, cancellationToken);
                }
            }
        }

        var immutableEdits = editsByFile.ToImmutableDictionary(
            pair => pair.Value.Values.First().SourceFilePath,
            pair => pair.Value.Values
                .OrderBy(edit => edit.Span.Start)
                .ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);

        ValidateProjectedEdits(immutableEdits);
        return new RazorProjectedChangeSet(immutableEdits);
    }

    internal static RazorProjectedChangeSet MergeChangeSets(IEnumerable<RazorProjectedChangeSet> changeSets)
    {
        var mergedEdits = new Dictionary<string, Dictionary<(int Start, int Length), RazorProjectedEdit>>(StringComparer.OrdinalIgnoreCase);

        foreach (var changeSet in changeSets)
        {
            foreach (var (sourceFilePath, edits) in changeSet.EditsByFile)
            {
                var normalizedPath = RefactoringHelpers.NormalizePathForComparison(sourceFilePath);
                if (!mergedEdits.TryGetValue(normalizedPath, out var fileEdits))
                {
                    fileEdits = new Dictionary<(int Start, int Length), RazorProjectedEdit>();
                    mergedEdits[normalizedPath] = fileEdits;
                }

                foreach (var edit in edits)
                {
                    var key = (edit.Span.Start, edit.Span.Length);
                    if (fileEdits.TryGetValue(key, out var existingEdit))
                    {
                        if (!string.Equals(existingEdit.NewText, edit.NewText, StringComparison.Ordinal))
                        {
                            throw new McpException(
                                $"Error: Razor rename produced conflicting edits for '{sourceFilePath}'");
                        }

                        continue;
                    }

                    fileEdits[key] = edit;
                }
            }
        }

        var immutableEdits = mergedEdits.ToImmutableDictionary(
            pair => pair.Value.Values.First().SourceFilePath,
            pair => pair.Value.Values
                .OrderBy(edit => edit.Span.Start)
                .ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);

        ValidateProjectedEdits(immutableEdits);
        return new RazorProjectedChangeSet(immutableEdits);
    }

    internal static async Task ApplyProjectedChangesAsync(
        RazorProjectedChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        foreach (var (sourceFilePath, edits) in changeSet.EditsByFile)
        {
            var (originalText, encoding) = await RefactoringHelpers.ReadFileWithEncodingAsync(sourceFilePath, cancellationToken);
            var sourceText = SourceText.From(originalText, encoding);
            var textChanges = edits
                .Select(edit => new TextChange(edit.Span, edit.NewText))
                .ToArray();
            var updatedText = sourceText.WithChanges(textChanges).ToString();
            await RefactoringHelpers.WriteFileWithEncodingAsync(sourceFilePath, updatedText, encoding, cancellationToken);
        }
    }

    private static async Task AddMappedEditAsync(
        Location location,
        string oldName,
        string newName,
        Dictionary<string, Dictionary<(int Start, int Length), RazorProjectedEdit>> editsByFile,
        Dictionary<string, SourceText> sourceTexts,
        CancellationToken cancellationToken)
    {
        if (!location.IsInSource || location.SourceTree == null)
            return;

        var mappedSpan = location.GetMappedLineSpan();
        if (!mappedSpan.HasMappedPath || string.IsNullOrWhiteSpace(mappedSpan.Path))
            return;

        var sourceKind = RazorDocumentClassifier.Classify(mappedSpan.Path);
        if (!RazorDocumentClassifier.IsRazorSource(sourceKind))
            return;

        var sourceFilePath = mappedSpan.Path;
        if (!sourceTexts.TryGetValue(sourceFilePath, out var sourceText))
        {
            var (sourceFileContent, _) = await RefactoringHelpers.ReadFileWithEncodingAsync(sourceFilePath, cancellationToken);
            sourceText = SourceText.From(sourceFileContent);
            sourceTexts[sourceFilePath] = sourceText;
        }

        var mappedSourceSpan = RazorGeneratedDocumentText.ToTextSpan(sourceText, mappedSpan);
        if (!TryResolveSourceEditSpan(sourceText, mappedSourceSpan, oldName, out var sourceSpan))
            return;

        var oldSourceSnippet = sourceText.ToString(sourceSpan);

        var normalizedPath = RefactoringHelpers.NormalizePathForComparison(sourceFilePath);
        if (!editsByFile.TryGetValue(normalizedPath, out var fileEdits))
        {
            fileEdits = new Dictionary<(int Start, int Length), RazorProjectedEdit>();
            editsByFile[normalizedPath] = fileEdits;
        }

        var key = (sourceSpan.Start, sourceSpan.Length);
        if (fileEdits.TryGetValue(key, out var existingEdit))
        {
            if (!string.Equals(existingEdit.NewText, newName, StringComparison.Ordinal))
            {
                throw new McpException(
                    $"Error: Razor rename produced conflicting edits for '{sourceFilePath}'");
            }

            return;
        }

        fileEdits[key] = new RazorProjectedEdit(sourceFilePath, sourceSpan, oldSourceSnippet, newName);
    }

    private static bool TryResolveSourceEditSpan(
        SourceText sourceText,
        TextSpan mappedSourceSpan,
        string oldName,
        out TextSpan resolvedSpan)
    {
        var mappedSnippet = sourceText.ToString(mappedSourceSpan);
        if (string.Equals(mappedSnippet, oldName, StringComparison.Ordinal))
        {
            resolvedSpan = mappedSourceSpan;
            return true;
        }

        var candidateOffsets = FindIdentifierOffsets(mappedSnippet, oldName);
        if (candidateOffsets.Count == 0)
        {
            resolvedSpan = default;
            return false;
        }

        if (candidateOffsets.Count > 1)
        {
            throw new McpException(
                "Error: Razor rename produced an ambiguous source-mapped edit span.");
        }

        resolvedSpan = new TextSpan(mappedSourceSpan.Start + candidateOffsets[0], oldName.Length);
        return true;
    }

    private static List<int> FindIdentifierOffsets(string text, string identifier)
    {
        var offsets = new List<int>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(identifier))
            return offsets;

        var startIndex = 0;
        while (startIndex < text.Length)
        {
            var index = text.IndexOf(identifier, startIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            var isStartBoundary = index == 0 || !IsIdentifierCharacter(text[index - 1]);
            var endIndex = index + identifier.Length;
            var isEndBoundary = endIndex == text.Length || !IsIdentifierCharacter(text[endIndex]);

            if (isStartBoundary && isEndBoundary)
                offsets.Add(index);

            startIndex = index + identifier.Length;
        }

        return offsets;
    }

    private static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value == '_' || value == '@';

    private static void ValidateProjectedEdits(
        ImmutableDictionary<string, ImmutableArray<RazorProjectedEdit>> editsByFile)
    {
        foreach (var (sourceFilePath, edits) in editsByFile)
        {
            for (var i = 1; i < edits.Length; i++)
            {
                var previous = edits[i - 1];
                var current = edits[i];
                if (current.Span.Start < previous.Span.End)
                {
                    throw new McpException(
                        $"Error: Razor rename produced overlapping source edits for '{sourceFilePath}'");
                }
            }
        }
    }
}
