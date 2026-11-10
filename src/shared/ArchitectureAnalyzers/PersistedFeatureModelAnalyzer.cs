using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PersistedFeatureModelAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE010";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Persisted feature models belong to Domain",
        "Persisted feature model '{0}' must belong to its owning Domain project",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeCollection, SyntaxKind.GenericName);
    }

    private static void AnalyzeCollection(SyntaxNodeAnalysisContext context)
    {
        var syntax = (GenericNameSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(syntax).Symbol;
        var type = symbol switch
        {
            INamedTypeSymbol named => named,
            IMethodSymbol method => method.ReturnType as INamedTypeSymbol,
            _ => null,
        };
        if (type is null)
            return;

        foreach (var model in CollectionModels(type)
                     .Distinct(SymbolEqualityComparer.Default)
                     .OfType<INamedTypeSymbol>())
            CheckModel(context, syntax, model);
    }

    private static IEnumerable<INamedTypeSymbol> CollectionModels(INamedTypeSymbol type)
    {
        if (CollectionModel(type) is { } direct)
        {
            yield return direct;
            yield break;
        }

        // Closed generic persistence wrappers can hide their typed collections behind private fields.
        if (!type.TypeArguments
                .OfType<INamedTypeSymbol>()
                .Any(argument =>
                    argument.ContainingNamespace
                        ?.ToDisplayString()
                        .StartsWith("Pole.Features.", StringComparison.Ordinal) == true))
            yield break;

        for (var current = type; current is not null; current = current.BaseType)
            foreach (var member in current.GetMembers())
            {
                var memberType = member switch
                {
                    IFieldSymbol field => field.Type,
                    IPropertySymbol property => property.Type,
                    _ => null,
                };
                if (memberType is INamedTypeSymbol named && CollectionModel(named) is { } model)
                    yield return model;
            }
    }

    private static INamedTypeSymbol? CollectionModel(INamedTypeSymbol type) =>
        type is { Name: "IMongoCollection", TypeArguments.Length: 1 } &&
        type.ContainingNamespace?.ToDisplayString() == "MongoDB.Driver"
            ? type.TypeArguments[0] as INamedTypeSymbol
            : null;

    private static void CheckModel(
        SyntaxNodeAnalysisContext context,
        GenericNameSyntax syntax,
        INamedTypeSymbol model)
    {
        var ns = model.ContainingNamespace?.ToDisplayString() ?? "";
        if (!ns.StartsWith("Pole.Features.", StringComparison.Ordinal))
            return;

        var parts = ns.Split('.');
        if (parts.Length < 3)
            return;

        var expected = "Pole.Features." + parts[2] + ".Domain";
        if ((ns == expected || ns.StartsWith(expected + ".", StringComparison.Ordinal)) &&
            model.ContainingAssembly?.Name == expected)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, syntax.GetLocation(), model.ToDisplayString()));
    }
}
