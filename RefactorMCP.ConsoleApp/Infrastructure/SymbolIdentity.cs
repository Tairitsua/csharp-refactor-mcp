using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

internal static class SymbolIdentity
{
    private static readonly SymbolDisplayFormat MetadataDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeExplicitInterface |
                       SymbolDisplayMemberOptions.IncludeParameters |
                       SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeExtensionThis |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut |
                          SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    internal static HashSet<string> CreateDeclarationKeys(IEnumerable<ISymbol> symbols)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            foreach (var key in GetDeclarationKeys(symbol))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    internal static IEnumerable<string> GetDeclarationKeys(ISymbol symbol)
    {
        foreach (var location in symbol.Locations.Where(location => location.IsInSource && location.SourceTree?.FilePath != null))
        {
            yield return CreateDeclarationKey(location);
        }
    }

    internal static HashSet<string> CreateComparisonKeys(ISymbol symbol) =>
        GetComparisonKeys(symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<string> GetComparisonKeys(ISymbol symbol)
    {
        foreach (var declarationKey in GetDeclarationKeys(symbol))
        {
            yield return $"decl:{declarationKey}";
        }

        yield return CreateMetadataKey(symbol);
    }

    internal static string CreateDeclarationKey(Location location)
    {
        var path = location.SourceTree?.FilePath ?? location.GetLineSpan().Path;
        var lineSpan = location.GetLineSpan();
        var normalizedPath = RefactoringHelpers.NormalizePathForComparison(path);
        return $"{normalizedPath}:{lineSpan.StartLinePosition.Line + 1:D6}:{lineSpan.StartLinePosition.Character + 1:D4}";
    }

    internal static string CreateMetadataKey(ISymbol symbol)
    {
        var originalDefinition = symbol.OriginalDefinition;
        var assemblyIdentity = originalDefinition.ContainingAssembly?.Identity?.GetDisplayName()
                               ?? originalDefinition.ContainingAssembly?.Identity?.Name
                               ?? string.Empty;
        var symbolDisplay = originalDefinition.ToDisplayString(MetadataDisplayFormat);
        return $"meta:{assemblyIdentity}:{symbolDisplay}";
    }
}
