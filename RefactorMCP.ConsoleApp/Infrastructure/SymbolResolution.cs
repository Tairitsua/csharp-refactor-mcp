using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol;
using System;
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
                    DiagnosticTrace.Log(
                        "SymbolResolution",
                        "Resolved symbol from position",
                        new
                        {
                            document = document.FilePath,
                            name,
                            line,
                            column,
                            symbol = symbolAtPosition.ToDisplayString()
                        });
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

        var selectedDeclaration = await SelectBestCandidateAsync(document, name, declarations, cancellationToken);
        DiagnosticTrace.Log(
            "SymbolResolution",
            "Resolved symbol from declarations",
            new
            {
                document = document.FilePath,
                name,
                symbol = selectedDeclaration.ToDisplayString(),
                declarationCount = declarations.Length
            });
        return selectedDeclaration;
    }

    internal static ISymbol? GetSymbolFromNode(SemanticModel semanticModel, SyntaxNode? node)
    {
        while (node != null)
        {
            var symbol = ResolveReferencedSymbol(semanticModel, node);
            if (symbol == null)
            {
                symbol = semanticModel.GetDeclaredSymbol(node);
            }

            if (symbol != null)
            {
                return symbol;
            }

            node = node.Parent;
        }

        return null;
    }

    private static ISymbol? ResolveReferencedSymbol(
        SemanticModel semanticModel,
        SyntaxNode node)
    {
        foreach (var lookupNode in GetSemanticLookupNodes(node))
        {
            var resolvedSymbol = ResolveSymbolFromInfoOrReceiverType(semanticModel, lookupNode);
            if (resolvedSymbol != null)
            {
                return resolvedSymbol;
            }
        }

        return null;
    }

    private static ISymbol? SelectBestCandidateSymbol(IEnumerable<ISymbol> candidateSymbols) =>
        candidateSymbols
            .OrderBy(GetCandidatePriority)
            .ThenBy(candidate => candidate.ToDisplayString(), StringComparer.Ordinal)
            .FirstOrDefault();

    private static IEnumerable<SyntaxNode> GetSemanticLookupNodes(SyntaxNode node)
    {
        yield return node;

        if (node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == node)
        {
            yield return memberAccess;

            if (memberAccess.Parent is InvocationExpressionSyntax invocation && invocation.Expression == memberAccess)
            {
                yield return invocation;
            }

            yield break;
        }

        if (node.Parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == node)
        {
            yield return memberBinding;

            if (memberBinding.Parent is InvocationExpressionSyntax invocation && invocation.Expression == memberBinding)
            {
                yield return invocation;
            }

            yield break;
        }

        if (node.Parent is QualifiedNameSyntax qualifiedName && qualifiedName.Right == node)
        {
            yield return qualifiedName;
            yield break;
        }

        if (node.Parent is AliasQualifiedNameSyntax aliasQualifiedName && aliasQualifiedName.Name == node)
        {
            yield return aliasQualifiedName;
        }
    }

    private static ISymbol? ResolveSymbolFromInfoOrReceiverType(
        SemanticModel semanticModel,
        SyntaxNode node)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? SelectBestCandidateSymbol(symbolInfo.CandidateSymbols);
        symbol = symbol is IAliasSymbol aliasSymbol
            ? aliasSymbol.Target
            : symbol;

        return symbol ?? ResolveSymbolFromReceiverType(semanticModel, node);
    }

    private static ISymbol? ResolveSymbolFromReceiverType(
        SemanticModel semanticModel,
        SyntaxNode node)
    {
        return node switch
        {
            IdentifierNameSyntax identifierName when identifierName.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == identifierName =>
                ResolveMemberAccessSymbol(semanticModel, memberAccess),
            IdentifierNameSyntax identifierName when identifierName.Parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == identifierName =>
                ResolveMemberBindingSymbol(semanticModel, memberBinding),
            MemberAccessExpressionSyntax memberAccess => ResolveMemberAccessSymbol(semanticModel, memberAccess),
            MemberBindingExpressionSyntax memberBinding => ResolveMemberBindingSymbol(semanticModel, memberBinding),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } => ResolveMemberAccessSymbol(semanticModel, memberAccess),
            InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax memberBinding } => ResolveMemberBindingSymbol(semanticModel, memberBinding),
            _ => null
        };
    }

    private static ISymbol? ResolveMemberAccessSymbol(
        SemanticModel semanticModel,
        MemberAccessExpressionSyntax memberAccess)
    {
        var receiverType = ResolveReceiverType(semanticModel, memberAccess.Expression);
        return ResolveMemberFromReceiverType(
            receiverType,
            memberAccess.Name.Identifier.ValueText,
            memberAccess.Parent as InvocationExpressionSyntax);
    }

    private static ISymbol? ResolveMemberBindingSymbol(
        SemanticModel semanticModel,
        MemberBindingExpressionSyntax memberBinding)
    {
        if (memberBinding.Parent?.Parent is not ConditionalAccessExpressionSyntax conditionalAccess)
        {
            return null;
        }

        var receiverType = ResolveReceiverType(semanticModel, conditionalAccess.Expression);
        return ResolveMemberFromReceiverType(
            receiverType,
            memberBinding.Name.Identifier.ValueText,
            memberBinding.Parent as InvocationExpressionSyntax);
    }

    private static ITypeSymbol? ResolveReceiverType(
        SemanticModel semanticModel,
        ExpressionSyntax expression)
    {
        var typeInfo = semanticModel.GetTypeInfo(expression);
        if (CanUseReceiverType(typeInfo.Type))
        {
            return typeInfo.Type;
        }

        if (CanUseReceiverType(typeInfo.ConvertedType))
        {
            return typeInfo.ConvertedType;
        }

        return ResolveReceiverTypeFromSyntaxHint(semanticModel, expression);
    }

    private static ITypeSymbol? ResolveReceiverTypeFromSyntaxHint(
        SemanticModel semanticModel,
        ExpressionSyntax expression)
    {
        return expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => ResolveReceiverType(semanticModel, parenthesized.Expression),
            CastExpressionSyntax castExpression => ResolveTypeSyntax(semanticModel, castExpression.Type),
            InvocationExpressionSyntax invocation => ResolveReceiverTypeFromInvocationHint(semanticModel, invocation),
            _ => null
        };
    }

    private static ITypeSymbol? ResolveReceiverTypeFromInvocationHint(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation)
    {
        return TryGetCastLikeTypeArgument(invocation.Expression, out var typeSyntax)
            ? ResolveTypeSyntax(semanticModel, typeSyntax)
            : null;
    }

    private static bool TryGetCastLikeTypeArgument(
        ExpressionSyntax expression,
        out TypeSyntax typeSyntax)
    {
        switch (expression)
        {
            case GenericNameSyntax genericName when IsCastLikeGenericMethod(genericName):
                typeSyntax = genericName.TypeArgumentList.Arguments[0];
                return true;
            case MemberAccessExpressionSyntax { Name: GenericNameSyntax genericName } when IsCastLikeGenericMethod(genericName):
                typeSyntax = genericName.TypeArgumentList.Arguments[0];
                return true;
            case MemberBindingExpressionSyntax { Name: GenericNameSyntax genericName } when IsCastLikeGenericMethod(genericName):
                typeSyntax = genericName.TypeArgumentList.Arguments[0];
                return true;
            default:
                typeSyntax = null!;
                return false;
        }
    }

    private static bool IsCastLikeGenericMethod(GenericNameSyntax genericName) =>
        string.Equals(genericName.Identifier.ValueText, "As", StringComparison.Ordinal) &&
        genericName.TypeArgumentList.Arguments.Count == 1;

    private static ITypeSymbol? ResolveTypeSyntax(
        SemanticModel semanticModel,
        TypeSyntax typeSyntax)
    {
        var typeInfo = semanticModel.GetTypeInfo(typeSyntax);
        if (typeInfo.Type != null && typeInfo.Type.TypeKind != TypeKind.Error)
        {
            return typeInfo.Type;
        }

        if (typeInfo.ConvertedType != null && typeInfo.ConvertedType.TypeKind != TypeKind.Error)
        {
            return typeInfo.ConvertedType;
        }

        var symbolInfo = semanticModel.GetSymbolInfo(typeSyntax);
        return symbolInfo.Symbol as ITypeSymbol ??
               symbolInfo.CandidateSymbols.OfType<ITypeSymbol>().FirstOrDefault();
    }

    private static bool CanUseReceiverType(ITypeSymbol? type) =>
        type != null &&
        type.TypeKind is not TypeKind.Error &&
        type.TypeKind is not TypeKind.Dynamic;

    private static ISymbol? ResolveMemberFromReceiverType(
        ITypeSymbol? receiverType,
        string memberName,
        InvocationExpressionSyntax? invocation)
    {
        if (receiverType == null)
        {
            return null;
        }

        var argumentCount = invocation?.ArgumentList.Arguments.Count;
        var candidateMembers = EnumerateCandidateTypes(receiverType)
            .SelectMany(type => type.GetMembers(memberName))
            .Where(member => member.Name == memberName)
            .ToArray();

        if (candidateMembers.Length == 0)
        {
            return null;
        }

        if (argumentCount.HasValue)
        {
            var methodCandidate = candidateMembers
                .OfType<IMethodSymbol>()
                .Where(method => method.Parameters.Length == argumentCount.Value)
                .OrderBy(GetCandidatePriority)
                .ThenBy(candidate => candidate.ToDisplayString(), StringComparer.Ordinal)
                .FirstOrDefault();
            if (methodCandidate != null)
            {
                return methodCandidate;
            }
        }

        return candidateMembers
            .OrderBy(GetCandidatePriority)
            .ThenBy(candidate => candidate.ToDisplayString(), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateCandidateTypes(ITypeSymbol receiverType)
    {
        if (receiverType is not INamedTypeSymbol namedType)
        {
            yield break;
        }

        var seenTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        for (var current = namedType; current != null; current = current.BaseType)
        {
            if (seenTypes.Add(current))
            {
                yield return current;
            }
        }

        foreach (var interfaceType in namedType.AllInterfaces)
        {
            if (seenTypes.Add(interfaceType))
            {
                yield return interfaceType;
            }
        }
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
