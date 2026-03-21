using ModelContextProtocol.Server;
using ModelContextProtocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.FindSymbols;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System;

[McpServerToolType]
public static class RenameSymbolTool
{
    [McpServerTool, Description("Rename a symbol across the solution using Roslyn")]
    public static async Task<string> RenameSymbol(
        [Description("Absolute path to the solution file (.sln or .slnx)")] string solutionPath,
        [Description("Path to the C# file containing the symbol")] string filePath,
        [Description("Current name of the symbol")] string oldName,
        [Description("New name for the symbol")] string newName,
        [Description("Line number of the symbol (1-based, optional)")] int? line = null,
        [Description("Column number of the symbol (1-based, optional)")] int? column = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            solutionPath = RefactoringHelpers.ResolveSolutionPath(solutionPath);
            var solution = await RefactoringHelpers.GetOrLoadSolution(solutionPath, cancellationToken);
            var razorContext = await RefactoringHelpers.GetOrLoadRazorSolutionContext(solutionPath, cancellationToken);
            var fileKind = RazorDocumentClassifier.Classify(filePath);

            if (fileKind is RazorDocumentKind.RazorImports or RazorDocumentKind.RazorViewImports)
                throw new McpException($"Error: RenameSymbol does not support starting from '{Path.GetFileName(filePath)}' in Phase 1");

            ISymbol? symbol;
            if (RazorDocumentClassifier.IsSupportedRenameEntryPoint(fileKind))
            {
                var razorResolution = await razorContext.FindSymbolAsync(
                    solution,
                    filePath,
                    oldName,
                    line,
                    column,
                    cancellationToken);

                if (razorResolution.Symbol == null &&
                    line.HasValue &&
                    column.HasValue &&
                    !razorResolution.HasMappedCSharpTokenAtPosition)
                {
                    throw new McpException(
                        "Error: RenameSymbol does not support the selected Razor span in Phase 1. Select a C#-backed identifier.");
                }

                symbol = razorResolution.Symbol;
            }
            else
            {
                var document = RefactoringHelpers.GetDocumentByPath(solution, filePath);
                if (document == null)
                    throw new McpException($"Error: File {filePath} not found in solution");

                symbol = await FindSymbol(document, oldName, line, column, cancellationToken);
            }

            if (symbol == null)
                throw new McpException($"Error: Symbol '{oldName}' not found");

            var options = new SymbolRenameOptions();
            var razorProjectedChanges = razorContext.HasRazorDocuments
                ? await RazorSourceMappingService.CreateChangeSetAsync(
                    symbol,
                    solution,
                    oldName,
                    newName,
                    cancellationToken)
                : new RazorProjectedChangeSet(
                    ImmutableDictionary<string, ImmutableArray<RazorProjectedEdit>>.Empty);

            var renamed = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, cancellationToken);
            var changedDocuments = await CollectChangedDocumentsAsync(solution, renamed, cancellationToken);
            var fileRenamePlans = await CreateFileRenamePlansAsync(
                solution,
                symbol,
                oldName,
                newName,
                changedDocuments,
                cancellationToken);
            ValidateFileRenamePlans(fileRenamePlans);
            var requiresCacheInvalidation = razorProjectedChanges.HasEdits || fileRenamePlans.Count > 0;

            foreach (var changedDocument in changedDocuments)
            {
                await RefactoringHelpers.WriteFileWithEncodingAsync(
                    changedDocument.FilePath,
                    changedDocument.UpdatedText,
                    changedDocument.Encoding,
                    cancellationToken);

                if (!requiresCacheInvalidation)
                    RefactoringHelpers.UpdateSolutionCache(changedDocument.Document);
            }

            await ApplyFileRenamePlansAsync(fileRenamePlans, cancellationToken);

            if (razorProjectedChanges.HasEdits)
            {
                await RazorSourceMappingService.ApplyProjectedChangesAsync(razorProjectedChanges, cancellationToken);
            }

            if (requiresCacheInvalidation)
            {
                RefactoringHelpers.InvalidateSolutionCaches(solutionPath);
            }

            return $"Successfully renamed '{oldName}' to '{newName}'";
        }
        catch (Exception ex)
        {
            throw new McpException($"Error renaming symbol: {ex.Message}", ex);
        }
    }

    internal static async Task<ISymbol?> FindSymbol(Document document, string name, int? line, int? column, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (model == null || root == null)
            return null;

        if (line.HasValue && column.HasValue)
        {
            var text = await document.GetTextAsync(cancellationToken);
            if (line.Value > 0 && line.Value <= text.Lines.Count && column.Value > 0)
            {
                var pos = text.Lines[line.Value - 1].Start + column.Value - 1;
                var symbolAtPosition = GetSymbolFromNode(model, root.FindToken(pos).Parent);
                if (symbolAtPosition != null && symbolAtPosition.Name == name)
                    return symbolAtPosition;
            }
        }

        foreach (var token in root.DescendantTokens().Where(t => t.ValueText == name || t.Text == name))
        {
            var symbolInDocument = GetSymbolFromNode(model, token.Parent);
            if (symbolInDocument != null && symbolInDocument.Name == name)
                return symbolInDocument;
        }

        var decls = await SymbolFinder.FindDeclarationsAsync(document.Project, name, false, cancellationToken);
        return decls.FirstOrDefault();
    }

    internal static ISymbol? GetSymbolFromNode(SemanticModel model, SyntaxNode? node)
    {
        while (node != null)
        {
            var symbol = model.GetDeclaredSymbol(node) ?? model.GetSymbolInfo(node).Symbol;
            if (symbol != null)
                return symbol;

            node = node.Parent;
        }

        return null;
    }

    private static async Task<IReadOnlyList<ChangedDocumentWriteback>> CollectChangedDocumentsAsync(
        Solution originalSolution,
        Solution renamedSolution,
        CancellationToken cancellationToken)
    {
        var writebacks = new List<ChangedDocumentWriteback>();
        var changes = renamedSolution.GetChanges(originalSolution);
        foreach (var projectChange in changes.GetProjectChanges())
        {
            foreach (var id in projectChange.GetChangedDocuments())
            {
                var newDoc = renamedSolution.GetDocument(id);
                if (newDoc?.FilePath == null)
                    continue;

                var text = await newDoc.GetTextAsync(cancellationToken);
                var (_, encoding) = await RefactoringHelpers.ReadFileWithEncodingAsync(newDoc.FilePath, cancellationToken);
                writebacks.Add(new ChangedDocumentWriteback(newDoc, newDoc.FilePath, text.ToString(), encoding));
            }
        }

        return writebacks;
    }

    private static async Task<IReadOnlyList<FileRenamePlan>> CreateFileRenamePlansAsync(
        Solution originalSolution,
        ISymbol symbol,
        string oldName,
        string newName,
        IReadOnlyList<ChangedDocumentWriteback> changedDocuments,
        CancellationToken cancellationToken)
    {
        if (symbol is not INamedTypeSymbol namedType)
            return [];

        var declarationDocuments = namedType.DeclaringSyntaxReferences
            .Select(reference => originalSolution.GetDocument(reference.SyntaxTree))
            .Where(document => document?.FilePath != null &&
                               RazorDocumentClassifier.Classify(document.FilePath) == RazorDocumentKind.CSharp)
            .Distinct()
            .ToArray();

        if (declarationDocuments.Length != 1)
            return [];

        var declarationDocument = declarationDocuments[0]!;
        var declarationFilePath = declarationDocument.FilePath!;
        if (!string.Equals(Path.GetFileNameWithoutExtension(declarationFilePath), oldName, StringComparison.OrdinalIgnoreCase))
            return [];

        var root = await declarationDocument.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var model = await declarationDocument.GetSemanticModelAsync(cancellationToken);
        if (root == null || model == null)
            return [];

        var topLevelTypeDeclarations = GetTopLevelTypeDeclarations(root);
        if (topLevelTypeDeclarations.Count != 1)
            return [];

        var declaredSymbol = GetDeclaredTypeSymbol(model, topLevelTypeDeclarations[0], cancellationToken);
        if (declaredSymbol == null ||
            !SymbolEqualityComparer.Default.Equals(declaredSymbol.OriginalDefinition, namedType.OriginalDefinition))
            return [];

        var changedDocument = changedDocuments.FirstOrDefault(doc =>
            RefactoringHelpers.PathEquals(doc.FilePath, declarationFilePath));
        if (changedDocument == null)
            return [];

        var renamedFilePath = Path.Combine(Path.GetDirectoryName(declarationFilePath)!, $"{newName}.cs");
        if (string.Equals(declarationFilePath, renamedFilePath, StringComparison.Ordinal))
            return [];

        return [new FileRenamePlan(declarationFilePath, renamedFilePath, changedDocument.UpdatedText)];
    }

    private static void ValidateFileRenamePlans(IReadOnlyList<FileRenamePlan> fileRenamePlans)
    {
        var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileRenamePlan in fileRenamePlans)
        {
            if (!plannedTargets.Add(RefactoringHelpers.NormalizePathForComparison(fileRenamePlan.NewFilePath)))
                throw new McpException($"Error: Multiple files would be renamed to '{fileRenamePlan.NewFilePath}'");

            if (!RefactoringHelpers.PathEquals(fileRenamePlan.OldFilePath, fileRenamePlan.NewFilePath) &&
                File.Exists(fileRenamePlan.NewFilePath))
            {
                throw new McpException($"Error: File {fileRenamePlan.NewFilePath} already exists");
            }
        }
    }

    private static Task ApplyFileRenamePlansAsync(
        IReadOnlyList<FileRenamePlan> fileRenamePlans,
        CancellationToken cancellationToken)
    {
        foreach (var fileRenamePlan in fileRenamePlans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (RefactoringHelpers.PathEquals(fileRenamePlan.OldFilePath, fileRenamePlan.NewFilePath))
            {
                var tempFilePath = Path.Combine(
                    Path.GetDirectoryName(fileRenamePlan.NewFilePath)!,
                    $".rename-{Guid.NewGuid():N}.tmp");
                File.Move(fileRenamePlan.OldFilePath, tempFilePath);
                File.Move(tempFilePath, fileRenamePlan.NewFilePath);
            }
            else
            {
                File.Move(fileRenamePlan.OldFilePath, fileRenamePlan.NewFilePath);
            }

            RefactoringHelpers.RenameFileCaches(
                fileRenamePlan.OldFilePath,
                fileRenamePlan.NewFilePath,
                fileRenamePlan.UpdatedText);
        }

        return Task.CompletedTask;
    }

    private static List<MemberDeclarationSyntax> GetTopLevelTypeDeclarations(CompilationUnitSyntax root)
    {
        var declarations = new List<MemberDeclarationSyntax>();
        CollectTopLevelTypeDeclarations(root.Members, declarations);
        return declarations;
    }

    private static void CollectTopLevelTypeDeclarations(
        SyntaxList<MemberDeclarationSyntax> members,
        List<MemberDeclarationSyntax> declarations)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case BaseTypeDeclarationSyntax:
                case DelegateDeclarationSyntax:
                    declarations.Add(member);
                    break;
                case NamespaceDeclarationSyntax namespaceDeclaration:
                    CollectTopLevelTypeDeclarations(namespaceDeclaration.Members, declarations);
                    break;
                case FileScopedNamespaceDeclarationSyntax fileScopedNamespace:
                    CollectTopLevelTypeDeclarations(fileScopedNamespace.Members, declarations);
                    break;
            }
        }
    }

    private static INamedTypeSymbol? GetDeclaredTypeSymbol(
        SemanticModel semanticModel,
        MemberDeclarationSyntax declaration,
        CancellationToken cancellationToken) =>
        declaration switch
        {
            BaseTypeDeclarationSyntax baseType => semanticModel.GetDeclaredSymbol(baseType, cancellationToken) as INamedTypeSymbol,
            DelegateDeclarationSyntax delegateDeclaration => semanticModel.GetDeclaredSymbol(delegateDeclaration, cancellationToken) as INamedTypeSymbol,
            _ => null
        };

    private sealed record ChangedDocumentWriteback(Document Document, string FilePath, string UpdatedText, Encoding Encoding);
    private sealed record FileRenamePlan(string OldFilePath, string NewFilePath, string UpdatedText);
}
