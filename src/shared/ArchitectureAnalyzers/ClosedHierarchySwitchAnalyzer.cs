using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ClosedHierarchySwitchAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE012";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Closed hierarchy switches enumerate their variants",
        "Switch over closed type '{0}' must name its variants instead of using a catch-all",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SwitchExpression, SyntaxKind.SwitchStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var expression = context.Node switch
        {
            SwitchExpressionSyntax value => value.GoverningExpression,
            SwitchStatementSyntax value => value.Expression,
            _ => null
        };
        if (expression is null ||
            context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type is not INamedTypeSymbol type ||
            !type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(context.CancellationToken)
                .ChildTokens().Any(token => token.ValueText == "closed")))
            return;

        IEnumerable<SyntaxNode> catchAll = context.Node switch
        {
            SwitchExpressionSyntax value => value.Arms.Where(arm => IsCatchAll(arm.Pattern))
                .Select(arm => (SyntaxNode)arm.Pattern),
            SwitchStatementSyntax value => value.Sections.SelectMany(section => section.Labels)
                .Where(label => label is DefaultSwitchLabelSyntax ||
                    label is CasePatternSwitchLabelSyntax pattern && IsCatchAll(pattern.Pattern)),
            _ => []
        };
        foreach (var node in catchAll)
            context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), type.Name));
    }

    private static bool IsCatchAll(PatternSyntax pattern) => pattern switch
    {
        DiscardPatternSyntax or VarPatternSyntax => true,
        ParenthesizedPatternSyntax nested => IsCatchAll(nested.Pattern),
        _ => false
    };
}
