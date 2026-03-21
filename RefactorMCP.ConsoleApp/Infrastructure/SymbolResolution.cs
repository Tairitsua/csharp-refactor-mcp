using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol;
using System.Collections.Generic;
using System.Linq;

internal static class SymbolResolution
{
    internal static async Task<ISymbol?> FindSymbolAsync(
        Document document,
        string name,
        int? line,
        int? column,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (semanticModel == null || root == null)
        {
            return null;
        }

        if (line.HasValue && column.HasValue)
        {
            var text = await document.GetTextAsync(cancellationToken);
            if (line.Value > 0 &&
                line.Value <= text.Lines.Count &&
                column.Value > 0 &&
                column.Value <= text.Lines[line.Value - 1].Span.Length + 1)
            {
                var position = text.Lines[line.Value - 1].Start + column.Value - 1;
                var symbolAtPosition = GetSymbolFromNode(semanticModel, root.FindToken(position).Parent);
                if (symbolAtPosition != null && symbolAtPosition.Name == name)
                {
                    return symbolAtPosition;
                }
            }
        }

        var candidates = CollectCandidates(document, semanticModel, root, name);
        if (candidates.Count > 0)
        {
            return await SelectBestCandidateAsync(document, name, candidates, cancellationToken);
        }

        var declarations = (await SymbolFinder.FindDeclarationsAsync(document.Project, name, false, cancellationToken))
            .Where(symbol => symbol.Name == name)
            .Select(symbol => new SymbolCandidate(symbol, false, GetCandidatePriority(symbol), 0))
            .ToArray();

        if (declarations.Length == 0)
        {
            return null;
        }

        return await SelectBestCandidateAsync(document, name, declarations, cancellationToken);
    }

    internal static ISymbol? GetSymbolFromNode(SemanticModel semanticModel, SyntaxNode? node)
    {
        while (node != null)
        {
            var symbol = semanticModel.GetDeclaredSymbol(node) ?? semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol != null)
            {
                return symbol;
            }

            node = node.Parent;
        }

        return null;
    }

    private static IReadOnlyList<SymbolCandidate> CollectCandidates(
        Document document,
        SemanticModel semanticModel,
        SyntaxNode root,
        string name)
    {
        var candidates = new List<SymbolCandidate>();
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var token in root.DescendantTokens().Where(token => token.ValueText == name || token.Text == name))
        {
            var symbol = GetSymbolFromNode(semanticModel, token.Parent);
            if (symbol == null || symbol.Name != name || !seenSymbols.Add(symbol))
            {
                continue;
            }

            candidates.Add(new SymbolCandidate(
                symbol,
                IsDeclaredInDocument(symbol, document),
                GetCandidatePriority(symbol),
                token.SpanStart));
        }

        return candidates;
    }

    private static async Task<ISymbol> SelectBestCandidateAsync(
        Document document,
        string name,
        IReadOnlyList<SymbolCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var declaredCandidates = candidates
            .Where(candidate => candidate.IsDeclaredInDocument)
            .ToArray();

        if (declaredCandidates.Length > 0)
        {
            return await SelectSingleCandidateAsync(
                document,
                name,
                declaredCandidates,
                preferDeclarationMessage: true,
                cancellationToken);
        }

        return await SelectSingleCandidateAsync(
            document,
            name,
            candidates,
            preferDeclarationMessage: false,
            cancellationToken);
    }

    private static async Task<ISymbol> SelectSingleCandidateAsync(
        Document document,
        string name,
        IReadOnlyList<SymbolCandidate> candidates,
        bool preferDeclarationMessage,
        CancellationToken cancellationToken)
    {
        var bestPriority = candidates.Min(candidate => candidate.Priority);
        var bestCandidates = candidates
            .Where(candidate => candidate.Priority == bestPriority)
            .OrderBy(candidate => candidate.Position)
            .ToArray();

        if (bestCandidates.Length == 1)
        {
            return bestCandidates[0].Symbol;
        }

        var canonicalCandidate = await TryCollapseRelatedCandidatesAsync(
            bestCandidates,
            document.Project.Solution,
            cancellationToken);
        if (canonicalCandidate != null)
        {
            return canonicalCandidate.Symbol;
        }

        var scopeDescription = preferDeclarationMessage
            ? "declarations"
            : "matches";
        throw new McpException(
            $"Error: Multiple {scopeDescription} named '{name}' were found in '{document.FilePath}'. Supply line and column to disambiguate.");
    }

    private static bool IsDeclaredInDocument(ISymbol symbol, Document document)
    {
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            if (document.FilePath != null &&
                string.Equals(syntaxReference.SyntaxTree.FilePath, document.FilePath, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<SymbolCandidate?> TryCollapseRelatedCandidatesAsync(
        IReadOnlyList<SymbolCandidate> candidates,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var closureCache = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        var remainingCandidates = candidates.ToList();
        var groups = new List<List<SymbolCandidate>>();

        while (remainingCandidates.Count > 0)
        {
            var group = new List<SymbolCandidate>();
            var queue = new Queue<SymbolCandidate>();
            queue.Enqueue(remainingCandidates[0]);
            remainingCandidates.RemoveAt(0);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                group.Add(current);

                for (var index = remainingCandidates.Count - 1; index >= 0; index--)
                {
                    var candidate = remainingCandidates[index];
                    if (!await SymbolClosure.AreRelatedAsync(
                            current.Symbol,
                            candidate.Symbol,
                            solution,
                            closureCache,
                            cancellationToken))
                    {
                        continue;
                    }

                    queue.Enqueue(candidate);
                    remainingCandidates.RemoveAt(index);
                }
            }

            groups.Add(group);
        }

        if (groups.Count != 1)
        {
            return null;
        }

        return groups[0]
            .OrderBy(candidate => candidate.Position)
            .First();
    }

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

    private sealed record SymbolCandidate(
        ISymbol Symbol,
        bool IsDeclaredInDocument,
        int Priority,
        int Position);
}
