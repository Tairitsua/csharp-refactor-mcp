using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
namespace RefactorMCP.ConsoleApp.SyntaxWalkers
{

    internal class PrivateFieldInfoWalker : CSharpSyntaxWalker
    {
        public Dictionary<string, TypeSyntax> Infos { get; } = new();

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            if (IsPrivateField(node))
            {
                foreach (var variable in node.Declaration.Variables)
                    Infos[variable.Identifier.ValueText] = node.Declaration.Type;
            }
            base.VisitFieldDeclaration(node);
        }

        private static bool IsPrivateField(FieldDeclarationSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.PrivateKeyword))
                return true;

            return !node.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.PublicKeyword) ||
                m.IsKind(SyntaxKind.ProtectedKeyword) ||
                m.IsKind(SyntaxKind.InternalKeyword));
        }
    }
}
