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
        "Mongo query API outside persistence",
        "Mongo query symbol '{0}' must only be used by a persistence capability",
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
        if (!context.Node.SyntaxTree.FilePath.Replace('\\', '/').Contains(".Application/", StringComparison.Ordinal) ||
            !RawMongoQuerySymbols.Contains(name.Identifier.ValueText) ||
            IsPersistenceSource(context.Node.SyntaxTree.FilePath))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            name.GetLocation(),
            name.Identifier.ValueText));
    }

    private static bool IsPersistenceSource(string filePath)
    {
        var normalizedPath = filePath.Replace('\\', '/');
        return normalizedPath.Contains(".Application/Persistence/", StringComparison.Ordinal);
    }
}
