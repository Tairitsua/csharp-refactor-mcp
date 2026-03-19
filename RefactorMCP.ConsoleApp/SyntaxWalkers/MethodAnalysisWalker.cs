using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
namespace RefactorMCP.ConsoleApp.SyntaxWalkers
{

    internal class MethodAnalysisWalker : CSharpSyntaxWalker
    {
        private readonly HashSet<string> _instanceMembers;
        private readonly HashSet<string> _methodNames;
        private readonly string _methodName;

        public bool UsesInstanceMembers { get; private set; }
        public bool CallsOtherMethods { get; private set; }
        public bool IsRecursive { get; private set; }

        public MethodAnalysisWalker(HashSet<string> instanceMembers, HashSet<string> methodNames, string methodName)
        {
            _instanceMembers = instanceMembers;
            _methodNames = methodNames;
            _methodName = methodName;
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (_instanceMembers.Contains(node.Identifier.ValueText) && IsImplicitOrThisQualifiedMemberAccess(node))
            {
                UsesInstanceMembers = true;
            }

            base.VisitIdentifierName(node);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (TryGetInvokedMethodName(node.Expression, out var methodName) && _methodNames.Contains(methodName))
            {
                if (methodName == _methodName)
                {
                    IsRecursive = true;
                }
                else
                {
                    CallsOtherMethods = true;
                }
            }
            base.VisitInvocationExpression(node);
        }

        private static bool IsImplicitOrThisQualifiedMemberAccess(IdentifierNameSyntax node)
        {
            return node.Parent switch
            {
                MemberAccessExpressionSyntax ma when ma.Name == node => ma.Expression is ThisExpressionSyntax,
                _ => true
            };
        }

        private static bool TryGetInvokedMethodName(ExpressionSyntax expression, out string methodName)
        {
            methodName = string.Empty;

            if (expression is IdentifierNameSyntax id)
            {
                methodName = id.Identifier.ValueText;
                return true;
            }

            if (expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is ThisExpressionSyntax &&
                memberAccess.Name is IdentifierNameSyntax memberName)
            {
                methodName = memberName.Identifier.ValueText;
                return true;
            }

            return false;
        }
    }
}
