using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MongoApplicationBoundaryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Mongo query API in an entry point",
        "Mongo query symbol '{0}' cannot be used in Endpoints, Consumers or Jobs",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly ImmutableHashSet<string> RawMongoQuerySymbols =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "IMongoCollection",
            "IAsyncCursor",
            "IFindFluent",
            "FilterDefinition",
            "UpdateDefinition",
            "Builders");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(
            AnalyzeSimpleName,
            SyntaxKind.IdentifierName,
            SyntaxKind.GenericName);
    }

    private static void AnalyzeSimpleName(SyntaxNodeAnalysisContext context)
    {
        var name = (SimpleNameSyntax)context.Node;
        if (!RawMongoQuerySymbols.Contains(name.Identifier.ValueText) ||
            MongoApplicationPaths.Classify(name.SyntaxTree.FilePath) != MongoApplicationPath.EntryPoint)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            name.GetLocation(),
            name.Identifier.ValueText));
    }
}
