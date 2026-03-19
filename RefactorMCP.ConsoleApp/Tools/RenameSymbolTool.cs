using ModelContextProtocol.Server;
using ModelContextProtocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.FindSymbols;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

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
            var solution = await RefactoringHelpers.GetOrLoadSolution(solutionPath, cancellationToken);
            var document = RefactoringHelpers.GetDocumentByPath(solution, filePath);
            if (document == null)
                throw new McpException($"Error: File {filePath} not found in solution");

            var symbol = await FindSymbol(document, oldName, line, column, cancellationToken);
            if (symbol == null)
                throw new McpException($"Error: Symbol '{oldName}' not found");

            var options = new SymbolRenameOptions();
            var renamed = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, cancellationToken);
            var changes = renamed.GetChanges(solution);
            foreach (var projectChange in changes.GetProjectChanges())
            {
                foreach (var id in projectChange.GetChangedDocuments())
                {
                    var newDoc = renamed.GetDocument(id)!;
                    var text = await newDoc.GetTextAsync(cancellationToken);
                    var (originalText, encoding) = await RefactoringHelpers.ReadFileWithEncodingAsync(newDoc.FilePath!, cancellationToken);
                    var updatedText = PreserveLineEndings(text.ToString(), originalText);
                    await RefactoringHelpers.WriteFileWithEncodingAsync(newDoc.FilePath!, updatedText, encoding, cancellationToken);
                    RefactoringHelpers.UpdateSolutionCache(newDoc);
                }
            }

            return $"Successfully renamed '{oldName}' to '{newName}'";
        }
        catch (Exception ex)
        {
            throw new McpException($"Error renaming symbol: {ex.Message}", ex);
        }
    }

    private static async Task<ISymbol?> FindSymbol(Document document, string name, int? line, int? column, CancellationToken cancellationToken)
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

    private static ISymbol? GetSymbolFromNode(SemanticModel model, SyntaxNode? node)
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

    private static string PreserveLineEndings(string updatedText, string originalText)
    {
        var normalizedUpdatedText = updatedText.Replace("\r\n", "\n");
        if (originalText.Contains("\r\n"))
            return normalizedUpdatedText.Replace("\n", "\r\n");

        return normalizedUpdatedText;
    }
}
