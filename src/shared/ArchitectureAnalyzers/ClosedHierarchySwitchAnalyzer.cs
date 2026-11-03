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
            context.SemanticModel.GetTypeInfo(expression,
                context.CancellationToken).Type is not INamedTypeSymbol type ||
            !ContainsClosedHierarchy(type))
            return;

        IEnumerable<SyntaxNode> catchAll = context.Node switch
        {
            SwitchExpressionSyntax value => value.Arms.Where(arm => IsCatchAll(arm.Pattern, type, context.SemanticModel))
                .Select(arm => (SyntaxNode)arm.Pattern),
            SwitchStatementSyntax value => value.Sections.SelectMany(section => section.Labels)
                .Where(label => label is DefaultSwitchLabelSyntax ||
                    label is CasePatternSwitchLabelSyntax pattern && IsCatchAll(pattern.Pattern, type, context.SemanticModel)),
            _ => []
        };
        foreach (var node in catchAll)
            context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation(), type.Name));
    }

    // The pinned preview compiler hides its IsClosedTypeAttribute from GetAttributes().
    // This accessor needs the preview semantic API; source and emitted-assembly tests cover it.
#pragma warning disable RSEXPERIMENTAL006
    private static bool IsClosedHierarchy(ITypeSymbol type) => type.IsClosed;
#pragma warning restore RSEXPERIMENTAL006

    private static bool ContainsClosedHierarchy(ITypeSymbol type) =>
        IsClosedHierarchy(type) || type is INamedTypeSymbol { IsTupleType: true } tuple &&
        tuple.TupleElements.Any(element => ContainsClosedHierarchy(element.Type));

    private static bool IsCatchAll(PatternSyntax pattern, ITypeSymbol input, SemanticModel semantics) => pattern switch
    {
        DiscardPatternSyntax or VarPatternSyntax => true,
        ParenthesizedPatternSyntax nested => IsCatchAll(nested.Pattern, input, semantics),
        TypePatternSyntax typed => MatchesBaseType(typed.Type, input, semantics),
        DeclarationPatternSyntax typed => MatchesBaseType(typed.Type, input, semantics),
        RecursivePatternSyntax recursive when input is INamedTypeSymbol { IsTupleType: true } tuple &&
            recursive.PositionalPatternClause is { } positional =>
            positional.Subpatterns.Count == tuple.TupleElements.Length &&
            positional.Subpatterns.Select((item, index) =>
                ContainsClosedHierarchy(tuple.TupleElements[index].Type) &&
                IsCatchAll(item.Pattern, tuple.TupleElements[index].Type, semantics)).Any(value => value),
        RecursivePatternSyntax { Type: { } type } => MatchesBaseType(type, input, semantics),
        RecursivePatternSyntax { PositionalPatternClause: null, PropertyPatternClause.Subpatterns.Count: 0 } => true,
        BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.OrPattern) =>
            IsCatchAll(binary.Left, input, semantics) || IsCatchAll(binary.Right, input, semantics),
        BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.AndPattern) =>
            IsCatchAll(binary.Left, input, semantics) && IsCatchAll(binary.Right, input, semantics),
        UnaryPatternSyntax => true,
        _ => false
    };

    private static bool MatchesBaseType(TypeSyntax syntax, ITypeSymbol input, SemanticModel semantics) =>
        semantics.GetTypeInfo(syntax).Type is { } matched &&
        ((CSharpCompilation)semantics.Compilation).ClassifyConversion(input, matched).IsImplicit;
}
