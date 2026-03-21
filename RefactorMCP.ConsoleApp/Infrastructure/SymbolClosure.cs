using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static class SymbolClosure
{
    internal static async Task<ISymbol> CanonicalizeAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var sourceDefinition = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, cancellationToken) ?? symbol;
        var originalDefinition = sourceDefinition.OriginalDefinition;
        return await SymbolFinder.FindSourceDefinitionAsync(originalDefinition, solution, cancellationToken) ?? originalDefinition;
    }

    internal static async Task<HashSet<ISymbol>> GetRelatedSymbolsAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var closureCache = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        return await GetRelatedSymbolsAsync(symbol, solution, closureCache, cancellationToken);
    }

    internal static async Task<HashSet<ISymbol>> GetRelatedSymbolsAsync(
        ISymbol symbol,
        Solution solution,
        Dictionary<ISymbol, HashSet<ISymbol>> closureCache,
        CancellationToken cancellationToken)
    {
        if (closureCache.TryGetValue(symbol, out var cachedClosure))
        {
            return cachedClosure;
        }

        var closure = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<ISymbol>();

        await EnqueueCanonicalAsync(symbol, queue, closure, solution, cancellationToken);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var relatedSymbol in await GetImmediateRelatedSymbolsAsync(current, solution, cancellationToken))
            {
                await EnqueueCanonicalAsync(relatedSymbol, queue, closure, solution, cancellationToken);
            }
        }

        closureCache[symbol] = closure;
        return closure;
    }

    internal static async Task<bool> AreRelatedAsync(
        ISymbol left,
        ISymbol right,
        Solution solution,
        Dictionary<ISymbol, HashSet<ISymbol>> closureCache,
        CancellationToken cancellationToken)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
        {
            return true;
        }

        var leftClosure = await GetRelatedSymbolsAsync(left, solution, closureCache, cancellationToken);
        return leftClosure.Contains(right);
    }

    internal static async Task<IReadOnlyList<ISymbol>> GetOrderedRenameTargetsAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var closure = await GetRelatedSymbolsAsync(symbol, solution, cancellationToken);
        return closure
            .Where(candidate => string.Equals(candidate.Name, symbol.Name, StringComparison.Ordinal))
            .OrderBy(GetRenamePriority)
            .ThenBy(GetStableSortKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ToDisplayString(), StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task EnqueueCanonicalAsync(
        ISymbol symbol,
        Queue<ISymbol> queue,
        HashSet<ISymbol> closure,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var canonicalSymbol = await CanonicalizeAsync(symbol, solution, cancellationToken);
        if (closure.Add(canonicalSymbol))
        {
            queue.Enqueue(canonicalSymbol);
        }
    }

    private static async Task<IEnumerable<ISymbol>> GetImmediateRelatedSymbolsAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var relatedSymbols = new List<ISymbol>();

        switch (symbol)
        {
            case IMethodSymbol methodSymbol:
                if (methodSymbol.OverriddenMethod != null)
                {
                    relatedSymbols.Add(methodSymbol.OverriddenMethod);
                }

                relatedSymbols.AddRange(methodSymbol.ExplicitInterfaceImplementations);
                break;

            case IPropertySymbol propertySymbol:
                if (propertySymbol.OverriddenProperty != null)
                {
                    relatedSymbols.Add(propertySymbol.OverriddenProperty);
                }

                relatedSymbols.AddRange(propertySymbol.ExplicitInterfaceImplementations);
                break;

            case IEventSymbol eventSymbol:
                if (eventSymbol.OverriddenEvent != null)
                {
                    relatedSymbols.Add(eventSymbol.OverriddenEvent);
                }

                relatedSymbols.AddRange(eventSymbol.ExplicitInterfaceImplementations);
                break;
        }

        relatedSymbols.AddRange(
            await SymbolFinder.FindImplementedInterfaceMembersAsync(symbol, solution, cancellationToken: cancellationToken));
        relatedSymbols.AddRange(
            await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken));
        relatedSymbols.AddRange(
            await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken));

        return relatedSymbols;
    }

    private static int GetRenamePriority(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol { ContainingType.TypeKind: TypeKind.Interface } => 0,
            IPropertySymbol { ContainingType.TypeKind: TypeKind.Interface } => 0,
            IEventSymbol { ContainingType.TypeKind: TypeKind.Interface } => 0,
            IMethodSymbol methodSymbol when methodSymbol.OverriddenMethod == null => 1,
            IPropertySymbol propertySymbol when propertySymbol.OverriddenProperty == null => 1,
            IEventSymbol eventSymbol when eventSymbol.OverriddenEvent == null => 1,
            _ => 2
        };

    private static string GetStableSortKey(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(currentLocation => currentLocation.IsInSource && currentLocation.SourceTree?.FilePath != null);
        if (location?.SourceTree?.FilePath == null)
        {
            return symbol.ToDisplayString();
        }

        var lineSpan = location.GetLineSpan();
        var normalizedPath = RefactoringHelpers.NormalizePathForComparison(location.SourceTree.FilePath);
        return $"{normalizedPath}:{lineSpan.StartLinePosition.Line + 1:D6}:{lineSpan.StartLinePosition.Character + 1:D4}";
    }
}
