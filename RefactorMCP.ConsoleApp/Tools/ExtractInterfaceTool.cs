using ModelContextProtocol.Server;
using ModelContextProtocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.ComponentModel;
using System.Threading;

[McpServerToolType]
public static class ExtractInterfaceTool
{
    [McpServerTool, Description("Extract a simple interface from a class")]
    public static async Task<string> ExtractInterface(
        [Description("Absolute path to the solution file (.sln or .slnx)")] string solutionPath,
        [Description("Path to the C# file containing the class")] string filePath,
        [Description("Name of the class to extract from")] string className,
        [Description("Comma separated list of member names to include")] string memberList,
        [Description("Path to write the generated interface file")] string interfaceFilePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await RefactoringHelpers.GetOrLoadSolution(solutionPath, cancellationToken);
            var document = RefactoringHelpers.GetDocumentByPath(solution, filePath);
            if (document == null)
                throw new McpException($"Error: File {filePath} not found in solution");

            var root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync(cancellationToken))!;
            var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.ValueText == className);
            if (classNode == null)
                throw new McpException($"Error: Class {className} not found");

            var chosen = memberList.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim()).ToHashSet(StringComparer.Ordinal);

            var members = new List<MemberDeclarationSyntax>();
            foreach (var member in classNode.Members)
            {
                var name = member switch
                {
                    MethodDeclarationSyntax m => m.Identifier.ValueText,
                    PropertyDeclarationSyntax p => p.Identifier.ValueText,
                    _ => null
                };
                if (name != null && chosen.Contains(name))
                {
                    switch (member)
                    {
                        case MethodDeclarationSyntax m:
                            members.Add(m.WithBody(null)
                                .WithExpressionBody(null)
                                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                                .WithModifiers(new SyntaxTokenList()));
                            break;
                        case PropertyDeclarationSyntax p:
                            AccessorListSyntax accessors;
                            if (p.AccessorList != null && p.AccessorList.Accessors.Count > 0)
                            {
                                accessors = SyntaxFactory.AccessorList(SyntaxFactory.List(
                                    p.AccessorList.Accessors.Select(a => a
                                        .WithBody(null)
                                        .WithExpressionBody(null)
                                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));
                            }
                            else
                            {
                                accessors = SyntaxFactory.AccessorList(
                                    SyntaxFactory.SingletonList(
                                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))));
                            }

                            members.Add(p.WithAccessorList(accessors).WithModifiers(new SyntaxTokenList()));
                            break;
                    }
                }
            }

            if (members.Count == 0)
                throw new McpException("Error: No matching members found");

            var interfaceName = Path.GetFileNameWithoutExtension(interfaceFilePath);
            var iface = SyntaxFactory.InterfaceDeclaration(interfaceName)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithMembers(SyntaxFactory.List(members));

            MemberDeclarationSyntax interfaceNode = iface;
            string? nsName = (classNode.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString();
            if (!string.IsNullOrEmpty(nsName))
            {
                interfaceNode = SyntaxFactory.FileScopedNamespaceDeclaration(
                        SyntaxFactory.ParseName(nsName))
                    .AddMembers(interfaceNode);
            }

            var ifaceUnit = SyntaxFactory.CompilationUnit()
                .WithUsings(root.Usings)
                .WithMembers(SyntaxFactory.SingletonList(interfaceNode))
                .NormalizeWhitespace();

            var encoding = await RefactoringHelpers.GetFileEncodingAsync(filePath, cancellationToken);
            await File.WriteAllTextAsync(interfaceFilePath, ifaceUnit.ToFullString(), encoding, cancellationToken);

            var interfaceType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(interfaceName));
            BaseListSyntax updatedBaseList;
            if (classNode.BaseList == null)
            {
                updatedBaseList = SyntaxFactory.BaseList(
                        SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(interfaceType))
                    .WithColonToken(SyntaxFactory.Token(SyntaxKind.ColonToken).WithTrailingTrivia(SyntaxFactory.Space));
            }
            else if (classNode.BaseList.Types.Any(t => t.Type.ToString() == interfaceName))
            {
                updatedBaseList = classNode.BaseList;
            }
            else
            {
                updatedBaseList = classNode.BaseList.WithTypes(classNode.BaseList.Types.Add(interfaceType));
            }

            var updatedClass = classNode.WithBaseList(updatedBaseList);
            var newRoot = root.ReplaceNode(classNode, updatedClass);
            var formatted = newRoot.NormalizeWhitespace().ToFullString();
            await File.WriteAllTextAsync(filePath, formatted, encoding, cancellationToken);
            RefactoringHelpers.UpdateFileCaches(filePath, formatted);
            RefactoringHelpers.AddDocumentToProject(document.Project, interfaceFilePath);
            return $"Successfully extracted interface '{interfaceName}' to {interfaceFilePath}";
        }
        catch (Exception ex)
        {
            throw new McpException($"Error extracting interface: {ex.Message}", ex);
        }
    }
}
