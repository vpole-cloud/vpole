using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainConstructorDefaultsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE011";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Domain constructors require explicit data",
        "Domain constructor parameter '{0}' must not have a default; initialize it in a factory or DefaultOptions",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeParameter, SyntaxKind.Parameter);
    }

    private static void AnalyzeParameter(SyntaxNodeAnalysisContext context)
    {
        var syntax = (ParameterSyntax)context.Node;
        if (syntax.Default is null ||
            context.SemanticModel.GetDeclaredSymbol(syntax) is not IParameterSymbol parameter ||
            parameter.ContainingSymbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            return;

        var parts = constructor.ContainingNamespace.ToDisplayString().Split('.');
        if (parts.Length < 4 || parts[0] != "Pole" || parts[1] != "Features" || parts[3] != "Domain")
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, syntax.Default.GetLocation(), parameter.Name));
    }
}
