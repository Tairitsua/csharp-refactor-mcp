using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
        var intrinsicClosureCache = new Dictionary<ISymbol, HashSet<ISymbol>>(SymbolEqualityComparer.Default);
        return await GetRelatedSymbolsAsync(symbol, solution, intrinsicClosureCache, cancellationToken);
    }

    internal static async Task<HashSet<ISymbol>> GetRelatedSymbolsAsync(
        ISymbol symbol,
        Solution solution,
        Dictionary<ISymbol, HashSet<ISymbol>> intrinsicClosureCache,
        CancellationToken cancellationToken)
    {
        var intrinsicClosure = await GetIntrinsicRelatedSymbolsAsync(
            symbol,
            solution,
            intrinsicClosureCache,
            cancellationToken);
        var expandedClosure = new HashSet<ISymbol>(intrinsicClosure, SymbolEqualityComparer.Default);

        if (ShouldExpandByDeclarations(symbol))
        {
            await ExpandBySameNameDeclarationsAsync(
                symbol,
                expandedClosure,
                solution,
                intrinsicClosureCache,
                cancellationToken);
        }

        DiagnosticTrace.Log(
            "SymbolClosure",
            "Built related symbol closure",
            new
            {
                symbol = symbol.ToDisplayString(),
                intrinsicCount = intrinsicClosure.Count,
                expandedCount = expandedClosure.Count,
                members = expandedClosure.Select(candidate => candidate.ToDisplayString()).OrderBy(value => value).ToArray()
            });

        return expandedClosure;
    }

    internal static async Task<bool> AreRelatedAsync(
        ISymbol left,
        ISymbol right,
        Solution solution,
        Dictionary<ISymbol, HashSet<ISymbol>> intrinsicClosureCache,
        CancellationToken cancellationToken)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
        {
            return true;
        }

        var leftClosure = await GetIntrinsicRelatedSymbolsAsync(left, solution, intrinsicClosureCache, cancellationToken);
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

    private static async Task<HashSet<ISymbol>> GetIntrinsicRelatedSymbolsAsync(
        ISymbol symbol,
        Solution solution,
        Dictionary<ISymbol, HashSet<ISymbol>> intrinsicClosureCache,
        CancellationToken cancellationToken)
    {
        var canonicalSymbol = await CanonicalizeAsync(symbol, solution, cancellationToken);
        if (intrinsicClosureCache.TryGetValue(canonicalSymbol, out var cachedClosure))
        {
            return cachedClosure;
        }

        var closure = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<ISymbol>();
        closure.Add(canonicalSymbol);
        queue.Enqueue(canonicalSymbol);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var relatedSymbol in await GetImmediateRelatedSymbolsAsync(current, solution, cancellationToken))
            {
                await EnqueueCanonicalAsync(relatedSymbol, queue, closure, solution, cancellationToken);
            }
        }

        foreach (var relatedSymbol in closure)
        {
            intrinsicClosureCache[relatedSymbol] = closure;
        }

        return closure;
    }

    private static async Task ExpandBySameNameDeclarationsAsync(
        ISymbol symbol,
        HashSet<ISymbol> closure,
        Solution solution,
        Dictionary<ISymbol, HashSet<ISymbol>> intrinsicClosureCache,
        CancellationToken cancellationToken)
    {
        var memberContractCache = new Dictionary<ISymbol, HashSet<string>>(SymbolEqualityComparer.Default);
        var candidates = await FindSameNameCandidatesAsync(symbol, solution, cancellationToken);
        DiagnosticTrace.Log(
            "SymbolClosure",
            "Found same-name candidates",
            new
            {
                symbol = symbol.ToDisplayString(),
                candidateCount = candidates.Count,
                candidates = candidates.Select(candidate => candidate.ToDisplayString()).OrderBy(value => value).ToArray()
            });
        var pendingCandidates = candidates
            .Where(candidate => !closure.Contains(candidate))
            .ToList();

        var addedNewCandidate = true;
        while (addedNewCandidate && pendingCandidates.Count > 0)
        {
            addedNewCandidate = false;

            for (var index = pendingCandidates.Count - 1; index >= 0; index--)
            {
                var candidate = pendingCandidates[index];
                if (!await IsRelatedToClosureAsync(
                        candidate,
                        closure,
                        solution,
                        intrinsicClosureCache,
                        memberContractCache,
                        cancellationToken))
                {
                    continue;
                }

                closure.Add(candidate);
                pendingCandidates.RemoveAt(index);
                addedNewCandidate = true;
                DiagnosticTrace.Log(
                    "SymbolClosure",
                    "Expanded closure with same-name candidate",
                    new
                    {
                        anchor = symbol.ToDisplayString(),
                        added = candidate.ToDisplayString()
                    });
            }
        }
    }

    private static async Task<IReadOnlyList<ISymbol>> FindSameNameCandidatesAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var seenCandidates = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var candidates = new List<ISymbol>();

        foreach (var project in solution.Projects)
        {
            var declarations = await SymbolFinder.FindDeclarationsAsync(
                project,
                symbol.Name,
                ignoreCase: false,
                cancellationToken);

            foreach (var declaration in declarations)
            {
                if (!IsSameKindCandidate(symbol, declaration))
                {
                    continue;
                }

                var canonicalDeclaration = await CanonicalizeAsync(declaration, solution, cancellationToken);
                if (seenCandidates.Add(canonicalDeclaration))
                {
                    candidates.Add(canonicalDeclaration);
                }
            }
        }

        return candidates;
    }

    private static async Task<bool> IsRelatedToClosureAsync(
        ISymbol candidate,
        HashSet<ISymbol> closure,
        Solution solution,
        Dictionary<ISymbol, HashSet<ISymbol>> intrinsicClosureCache,
        Dictionary<ISymbol, HashSet<string>> memberContractCache,
        CancellationToken cancellationToken)
    {
        foreach (var existing in closure)
        {
            if (existing is IMethodSymbol existingMethod &&
                candidate is IMethodSymbol candidateMethod)
            {
                if (await AreMethodsRelatedByContractsAsync(
                        existingMethod,
                        candidateMethod,
                        solution,
                        memberContractCache,
                        cancellationToken))
                {
                    return true;
                }

                if (await CanUseContainingTypeMethodFallbackAsync(
                        existingMethod,
                        candidateMethod,
                        solution,
                        cancellationToken) &&
                    await AreRelatedByContainingTypeAsync(existing, candidate, solution, cancellationToken))
                {
                    return true;
                }

                continue;
            }

            if (await AreRelatedAsync(existing, candidate, solution, intrinsicClosureCache, cancellationToken) ||
                await AreRelatedAsync(candidate, existing, solution, intrinsicClosureCache, cancellationToken))
            {
                return true;
            }

            if (await AreRelatedByContainingTypeAsync(existing, candidate, solution, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldExpandByDeclarations(ISymbol symbol) =>
        symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol;

    private static bool IsSameKindCandidate(ISymbol symbol, ISymbol candidate) =>
        (symbol, candidate) switch
        {
            (IMethodSymbol leftMethod, IMethodSymbol rightMethod) =>
                leftMethod.MethodKind == rightMethod.MethodKind &&
                leftMethod.Parameters.Length == rightMethod.Parameters.Length,
            (IPropertySymbol, IPropertySymbol) => true,
            (IEventSymbol, IEventSymbol) => true,
            _ => false
        };

    private static async Task<bool> AreRelatedByContainingTypeAsync(
        ISymbol left,
        ISymbol right,
        Solution solution,
        CancellationToken cancellationToken)
    {
        if (!HaveCompatibleMemberShape(left, right))
        {
            return false;
        }

        var leftType = await GetCanonicalContainingTypeAsync(left, solution, cancellationToken);
        var rightType = await GetCanonicalContainingTypeAsync(right, solution, cancellationToken);
        if (leftType == null || rightType == null)
        {
            return false;
        }

        if (left is IMethodSymbol && right is IMethodSymbol && TypeEquals(leftType, rightType))
        {
            return false;
        }

        if (TypesRelated(leftType, rightType) || TypesRelated(rightType, leftType))
        {
            return true;
        }

        return await TypesRelatedByDeclarationsAsync(leftType, rightType, solution, cancellationToken) ||
               await TypesRelatedByDeclarationsAsync(rightType, leftType, solution, cancellationToken);
    }

    private static async Task<bool> AreMethodsRelatedByContractsAsync(
        IMethodSymbol left,
        IMethodSymbol right,
        Solution solution,
        Dictionary<ISymbol, HashSet<string>> memberContractCache,
        CancellationToken cancellationToken)
    {
        if (!HaveCompatibleMemberShape(left, right))
        {
            return false;
        }

        var leftKeys = await GetMethodContractKeysAsync(left, solution, memberContractCache, cancellationToken);
        var rightKeys = await GetMethodContractKeysAsync(right, solution, memberContractCache, cancellationToken);
        return leftKeys.Overlaps(rightKeys);
    }

    private static async Task<bool> CanUseContainingTypeMethodFallbackAsync(
        IMethodSymbol existing,
        IMethodSymbol candidate,
        Solution solution,
        CancellationToken cancellationToken)
    {
        if (candidate.ContainingType == null || existing.ContainingType == null)
        {
            return false;
        }

        if (candidate.IsOverride || candidate.IsAbstract || candidate.ContainingType.TypeKind == TypeKind.Interface)
        {
            return true;
        }

        if (existing.ContainingType.TypeKind != TypeKind.Interface)
        {
            return false;
        }

        return await DirectlyDeclaresRelatedTypeAsync(
            candidate.ContainingType,
            existing.ContainingType,
            solution,
            cancellationToken);
    }

    private static async Task<HashSet<string>> GetMethodContractKeysAsync(
        IMethodSymbol method,
        Solution solution,
        Dictionary<ISymbol, HashSet<string>> memberContractCache,
        CancellationToken cancellationToken)
    {
        var canonicalMethod = await CanonicalizeAsync(method, solution, cancellationToken);
        if (memberContractCache.TryGetValue(canonicalMethod, out var cachedKeys))
        {
            return cachedKeys;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSymbolComparisonKeys(keys, canonicalMethod);

        if (canonicalMethod is IMethodSymbol canonicalMethodSymbol)
        {
            for (var current = canonicalMethodSymbol.OverriddenMethod; current != null; current = current.OverriddenMethod)
            {
                AddSymbolComparisonKeys(
                    keys,
                    await CanonicalizeAsync(current, solution, cancellationToken));
            }

            foreach (var explicitInterfaceImplementation in canonicalMethodSymbol.ExplicitInterfaceImplementations)
            {
                AddSymbolComparisonKeys(
                    keys,
                    await CanonicalizeAsync(explicitInterfaceImplementation, solution, cancellationToken));
            }

            await AddImplementedInterfaceContractKeysAsync(
                canonicalMethodSymbol,
                keys,
                solution,
                cancellationToken);
        }

        memberContractCache[canonicalMethod] = keys;
        return keys;
    }

    private static async Task AddImplementedInterfaceContractKeysAsync(
        IMethodSymbol method,
        HashSet<string> keys,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var containingType = method.ContainingType;
        if (containingType == null)
        {
            return;
        }

        foreach (var interfaceType in containingType.AllInterfaces)
        {
            foreach (var interfaceMethod in interfaceType.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                if (!HaveCompatibleMemberShape(method, interfaceMethod))
                {
                    continue;
                }

                if (containingType.FindImplementationForInterfaceMember(interfaceMethod) is not IMethodSymbol implementation)
                {
                    continue;
                }

                var canonicalImplementation = await CanonicalizeAsync(implementation, solution, cancellationToken);
                if (!HaveMatchingComparisonKey(canonicalImplementation, method))
                {
                    continue;
                }

                AddSymbolComparisonKeys(
                    keys,
                    await CanonicalizeAsync(interfaceMethod, solution, cancellationToken));
            }
        }
    }

    private static void AddSymbolComparisonKeys(HashSet<string> keys, ISymbol symbol)
    {
        foreach (var key in SymbolIdentity.GetComparisonKeys(symbol))
        {
            keys.Add(key);
        }
    }

    private static bool HaveCompatibleMemberShape(ISymbol left, ISymbol right) =>
        (left, right) switch
        {
            (IMethodSymbol leftMethod, IMethodSymbol rightMethod) =>
                leftMethod.MethodKind == rightMethod.MethodKind &&
                leftMethod.Parameters.Length == rightMethod.Parameters.Length,
            (IPropertySymbol, IPropertySymbol) => true,
            (IEventSymbol, IEventSymbol) => true,
            _ => false
        };

    private static async Task<INamedTypeSymbol?> GetCanonicalContainingTypeAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        if (symbol.ContainingType == null)
        {
            return null;
        }

        return await CanonicalizeAsync(symbol.ContainingType, solution, cancellationToken) as INamedTypeSymbol;
    }

    private static bool TypesRelated(INamedTypeSymbol candidate, INamedTypeSymbol target)
    {
        if (TypeEquals(candidate, target))
        {
            return true;
        }

        foreach (var baseType in EnumerateBaseTypes(candidate))
        {
            if (TypeEquals(baseType, target))
            {
                return true;
            }
        }

        foreach (var interfaceType in candidate.AllInterfaces)
        {
            if (TypeEquals(interfaceType, target))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateBaseTypes(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool TypeEquals(INamedTypeSymbol left, INamedTypeSymbol right) =>
        HaveMatchingComparisonKey(left, right);

    private static bool HaveMatchingComparisonKey(ISymbol left, ISymbol right)
    {
        var rightKeys = SymbolIdentity.CreateComparisonKeys(right);
        return SymbolIdentity.GetComparisonKeys(left).Any(rightKeys.Contains);
    }

    private static async Task<bool> TypesRelatedByDeclarationsAsync(
        INamedTypeSymbol candidate,
        INamedTypeSymbol target,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var targetKeys = GetTypeMatchKeys(target);

        foreach (var syntaxReference in candidate.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await syntaxReference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax typeDeclaration ||
                typeDeclaration.BaseList == null)
            {
                continue;
            }

            var document = solution.GetDocument(syntaxReference.SyntaxTree);
            var semanticModel = document == null
                ? null
                : await document.GetSemanticModelAsync(cancellationToken);

            foreach (var baseType in typeDeclaration.BaseList.Types)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var resolvedType = ResolveNamedType(semanticModel, baseType.Type, cancellationToken);
                if (resolvedType != null && TypeEquals(resolvedType, target))
                {
                    DiagnosticTrace.Log(
                        "SymbolClosure",
                        "Matched containing type by declared hierarchy symbol",
                        new
                        {
                            candidate = candidate.ToDisplayString(),
                            target = target.ToDisplayString(),
                            relatedType = resolvedType.ToDisplayString()
                        });
                    return true;
                }

                var normalizedReference = NormalizeTypeReference(baseType.Type.ToString());
                if (normalizedReference.Length == 0)
                {
                    continue;
                }

                if (targetKeys.Contains(normalizedReference) ||
                    targetKeys.Contains(GetSimpleTypeName(normalizedReference)))
                {
                    DiagnosticTrace.Log(
                        "SymbolClosure",
                        "Matched containing type by declared hierarchy text",
                        new
                        {
                            candidate = candidate.ToDisplayString(),
                            target = target.ToDisplayString(),
                            declaredType = normalizedReference
                        });
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<bool> DirectlyDeclaresRelatedTypeAsync(
        INamedTypeSymbol candidate,
        INamedTypeSymbol target,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var targetKeys = GetTypeMatchKeys(target);

        foreach (var syntaxReference in candidate.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await syntaxReference.GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax typeDeclaration ||
                typeDeclaration.BaseList == null)
            {
                continue;
            }

            var document = solution.GetDocument(syntaxReference.SyntaxTree);
            var semanticModel = document == null
                ? null
                : await document.GetSemanticModelAsync(cancellationToken);

            foreach (var baseType in typeDeclaration.BaseList.Types)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var resolvedType = ResolveNamedType(semanticModel, baseType.Type, cancellationToken);
                if (resolvedType != null && TypeEquals(resolvedType, target))
                {
                    return true;
                }

                var normalizedReference = NormalizeTypeReference(baseType.Type.ToString());
                if (normalizedReference.Length == 0)
                {
                    continue;
                }

                if (targetKeys.Contains(normalizedReference) ||
                    targetKeys.Contains(GetSimpleTypeName(normalizedReference)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static INamedTypeSymbol? ResolveNamedType(
        SemanticModel? semanticModel,
        TypeSyntax typeSyntax,
        CancellationToken cancellationToken)
    {
        if (semanticModel == null)
        {
            return null;
        }

        var symbol = semanticModel.GetSymbolInfo(typeSyntax, cancellationToken).Symbol;
        if (symbol is IAliasSymbol aliasSymbol)
        {
            symbol = aliasSymbol.Target;
        }

        if (symbol is INamedTypeSymbol namedTypeSymbol)
        {
            return namedTypeSymbol;
        }

        return semanticModel.GetTypeInfo(typeSyntax, cancellationToken).Type as INamedTypeSymbol;
    }

    private static HashSet<string> GetTypeMatchKeys(INamedTypeSymbol type)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        AddTypeMatchKeys(keys, type);
        AddTypeMatchKeys(keys, type.OriginalDefinition);
        return keys;
    }

    private static void AddTypeMatchKeys(HashSet<string> keys, INamedTypeSymbol type)
    {
        keys.Add(type.Name);

        var qualifiedTypeName = GetQualifiedTypeName(type);
        if (qualifiedTypeName.Length > 0)
        {
            keys.Add(qualifiedTypeName);
            keys.Add(NormalizeTypeReference(qualifiedTypeName));
        }
    }

    private static string GetQualifiedTypeName(INamedTypeSymbol type)
    {
        var containingTypes = new Stack<string>();
        for (var current = type; current != null; current = current.ContainingType)
        {
            containingTypes.Push(current.Name);
        }

        var typeName = string.Join(".", containingTypes);
        var containingNamespace = type.ContainingNamespace;
        if (containingNamespace == null || containingNamespace.IsGlobalNamespace)
        {
            return typeName;
        }

        return $"{containingNamespace.ToDisplayString()}.{typeName}";
    }

    private static string NormalizeTypeReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var genericDepth = 0;

        foreach (var current in value)
        {
            switch (current)
            {
                case '<':
                    genericDepth++;
                    continue;
                case '>':
                    if (genericDepth > 0)
                    {
                        genericDepth--;
                    }
                    continue;
                case '?':
                    continue;
            }

            if (genericDepth > 0 || char.IsWhiteSpace(current))
            {
                continue;
            }

            builder.Append(current);
        }

        return builder
            .ToString()
            .Replace("global::", string.Empty, StringComparison.Ordinal);
    }

    private static string GetSimpleTypeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var separatorIndex = value.LastIndexOf('.');
        return separatorIndex >= 0
            ? value[(separatorIndex + 1)..]
            : value;
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

        if (symbol is IMethodSymbol method)
        {
            var memberContractCache = new Dictionary<ISymbol, HashSet<string>>(SymbolEqualityComparer.Default);
            return await FilterMethodRelatedSymbolsAsync(
                method,
                relatedSymbols,
                solution,
                memberContractCache,
                cancellationToken);
        }

        return relatedSymbols;
    }

    private static async Task<IEnumerable<ISymbol>> FilterMethodRelatedSymbolsAsync(
        IMethodSymbol anchor,
        IEnumerable<ISymbol> relatedSymbols,
        Solution solution,
        Dictionary<ISymbol, HashSet<string>> memberContractCache,
        CancellationToken cancellationToken)
    {
        var filteredSymbols = new List<ISymbol>();

        foreach (var relatedSymbol in relatedSymbols)
        {
            if (relatedSymbol is not IMethodSymbol relatedMethod)
            {
                continue;
            }

            if (!await AreMethodsRelatedByContractsAsync(
                    anchor,
                    relatedMethod,
                    solution,
                    memberContractCache,
                    cancellationToken))
            {
                continue;
            }

            filteredSymbols.Add(relatedSymbol);
        }

        return filteredSymbols;
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
