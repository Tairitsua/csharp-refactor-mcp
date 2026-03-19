using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

internal class FeatureFlagRewriter : CSharpSyntaxRewriter
{
    private readonly string _flagName;
    private readonly string _interfaceName;
    private readonly string _strategyField;
    private readonly string _strategyParameter;
    private bool _done;
    private IfStatementSyntax? _targetIf;
    public SyntaxList<MemberDeclarationSyntax> GeneratedMembers { get; private set; }

    public FeatureFlagRewriter(string flagName)
    {
        _flagName = flagName;
        _interfaceName = $"I{flagName}Strategy";
        _strategyField = $"_{char.ToLower(flagName[0])}{flagName.Substring(1)}Strategy";
        _strategyParameter = _strategyField.TrimStart('_');
        GeneratedMembers = new SyntaxList<MemberDeclarationSyntax>();
    }

    private static bool IsFlagCheck(ExpressionSyntax condition, string flag)
    {
        if (condition is InvocationExpressionSyntax inv &&
            inv.Expression is MemberAccessExpressionSyntax ma &&
            ma.Name.Identifier.ValueText == "IsEnabled" &&
            inv.ArgumentList.Arguments.Count == 1)
        {
            var arg = inv.ArgumentList.Arguments[0].Expression;
            if (arg is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                return lit.Token.ValueText == flag;
        }
        return false;
    }

    public override SyntaxNode VisitIfStatement(IfStatementSyntax node)
    {
        if (!_done && IsFlagCheck(node.Condition, _flagName))
        {
            _done = true;
            _targetIf = node;
            var applyCall = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(_strategyField),
                        SyntaxFactory.IdentifierName("Apply"))));
            return applyCall.WithTriviaFrom(node);
        }
        return base.VisitIfStatement(node)!;
    }

    public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        if (_done && _targetIf != null && node.Span.Contains(_targetIf.Span))
        {
            visited = AddStrategyField(visited);
            visited = UpdateConstructors(visited);

            if (!GeneratedMembers.Any())
                GeneratedMembers = CreateStrategyTypes();

            visited = ((ClassDeclarationSyntax)visited.NormalizeWhitespace())
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
        return visited;
    }

    private SyntaxList<MemberDeclarationSyntax> CreateStrategyTypes()
    {
        var interfaceMethod = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "Apply")
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        var applyMethod = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                "Apply")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithBody(GetTrueBlock());

        var iface = SyntaxFactory.InterfaceDeclaration(_interfaceName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddMembers(interfaceMethod);

        var strat = SyntaxFactory.ClassDeclaration(_flagName + "Strategy")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName(_interfaceName)))
            .AddMembers(applyMethod);

        var noBody = GetFalseBlock();
        var noStrat = SyntaxFactory.ClassDeclaration("No" + _flagName + "Strategy")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName(_interfaceName)))
            .AddMembers(applyMethod.WithBody(noBody));

        return SyntaxFactory.List(new MemberDeclarationSyntax[]
        {
            iface.NormalizeWhitespace(),
            strat.NormalizeWhitespace(),
            noStrat.NormalizeWhitespace()
        });
    }

    private BlockSyntax GetTrueBlock()
    {
        if (_targetIf!.Statement is BlockSyntax block)
            return block;
        return SyntaxFactory.Block(_targetIf.Statement);
    }

    private BlockSyntax GetFalseBlock()
    {
        if (_targetIf!.Else == null) return SyntaxFactory.Block();
        var stmt = _targetIf.Else.Statement;
        if (stmt is BlockSyntax b) return b;
        return SyntaxFactory.Block(stmt);
    }

    private ClassDeclarationSyntax AddStrategyField(ClassDeclarationSyntax node)
    {
        if (node.Members.OfType<FieldDeclarationSyntax>()
            .Any(field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == _strategyField)))
        {
            return node;
        }

        var fieldDecl = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName(_interfaceName))
                .AddVariables(SyntaxFactory.VariableDeclarator(_strategyField)))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword), SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        return node.WithMembers(node.Members.Insert(0, fieldDecl));
    }

    private ClassDeclarationSyntax UpdateConstructors(ClassDeclarationSyntax node)
    {
        var constructors = node.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        if (constructors.Count == 0)
            return node.AddMembers(CreateConstructor(node.Identifier.ValueText));

        var replacements = constructors.ToDictionary(ctor => ctor, AddStrategyDependency);
        return node.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
    }

    private ConstructorDeclarationSyntax AddStrategyDependency(ConstructorDeclarationSyntax constructor)
    {
        var updated = constructor;

        if (!updated.ParameterList.Parameters.Any(p => p.Identifier.ValueText == _strategyParameter))
        {
            updated = updated.AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(_strategyParameter))
                    .WithType(SyntaxFactory.IdentifierName(_interfaceName)));
        }

        var assignment = CreateAssignmentStatement();
        var body = updated.Body ?? SyntaxFactory.Block();

        if (!body.Statements.OfType<ExpressionStatementSyntax>().Any(IsStrategyAssignment))
            body = body.WithStatements(body.Statements.Insert(0, assignment));

        return updated.WithBody(body);
    }

    private ConstructorDeclarationSyntax CreateConstructor(string className)
    {
        return SyntaxFactory.ConstructorDeclaration(className)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(_strategyParameter))
                    .WithType(SyntaxFactory.IdentifierName(_interfaceName)))
            .WithBody(SyntaxFactory.Block(CreateAssignmentStatement()));
    }

    private ExpressionStatementSyntax CreateAssignmentStatement()
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(_strategyField),
                SyntaxFactory.IdentifierName(_strategyParameter)));
    }

    private bool IsStrategyAssignment(ExpressionStatementSyntax statement)
    {
        return statement.Expression is AssignmentExpressionSyntax assignment &&
               assignment.Left is IdentifierNameSyntax left &&
               assignment.Right is IdentifierNameSyntax right &&
               left.Identifier.ValueText == _strategyField &&
               right.Identifier.ValueText == _strategyParameter;
    }
}
