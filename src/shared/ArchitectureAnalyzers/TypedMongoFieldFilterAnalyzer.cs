using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TypedMongoFieldFilterAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE013";
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Runtime Mongo filters use typed fields",
        "Use a typed field expression instead of the string field '{0}'",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (invocation.TargetMethod.ContainingType.Name != "FilterDefinitionBuilder" ||
            invocation.TargetMethod.ContainingNamespace.ToDisplayString() != "MongoDB.Driver")
            return;
        var path = invocation.Syntax.SyntaxTree.FilePath.Replace('\\', '/');
        if (!path.Contains(".Application/Persistence/") || path.Contains("/Migrations/"))
            return;
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Name == "field" && argument.Syntax is ArgumentSyntax
                { Expression: LiteralExpressionSyntax literal } && literal.IsKind(SyntaxKind.StringLiteralExpression))
                context.ReportDiagnostic(Diagnostic.Create(Rule, literal.GetLocation(), literal.Token.ValueText));
        }
    }
}
