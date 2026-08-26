using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class ArchitectureBoundaryTests
{
    private static readonly string[] LegacyMongoContextSymbols =
    [
        "IMongoDbContext",
        "IEventDbContext",
        "MongoDbContext",
        "StoreEventAsync",
        "ClientSessionHandleExtensions"
    ];

    private static readonly string[] LegacyResultCompatibilitySymbols =
    [
        "IMediatorHandler",
        "LegacyResultRejected",
        "RunMongoTransaction",
        "ISuccess",
        "IFailure"
    ];

    private static readonly string[] FeatureRegistrationMethods =
    [
        "AddOutboxFeature",
        "AddResourceGroupsFeature",
        "AddFunctionAppsFeature",
        "AddFunctionRuntimeFeature",
        "AddResourceDeploymentsFeature",
        "AddTenantsFeature",
        "AddDomainsFeature",
        "AddComputeProfilesFeature",
        "AddStorageProfilesFeature",
        "AddRedisServicesFeature",
        "AddPostgresFeature",
        "AddWebServicesFeature",
        "AddQuotasFeature",
        "AddDeploymentTargetsFeature",
        "AddSearchFeature",
        "AddGitProvidersFeature",
        "AddContainerRegistriesFeature",
        "AddSourceBuildsFeature",
        "AddUsersFeature"
    ];

    [Fact]
    public void ProductionSources_DoNotReferenceRemovedMongoContexts()
    {
        var violations = ArchitectureSources.ProductionCSharp()
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<SimpleNameSyntax>()
                .Where(name => LegacyMongoContextSymbols.Contains(name.Identifier.ValueText))
                .Select(name => ArchitectureSources.Describe(
                    source,
                    name,
                    $"removed persistence symbol '{name.Identifier.ValueText}' is forbidden")))
            .ToArray();

        AssertNoViolations(violations);
    }

    [Fact]
    public void RepositorySources_DoNotReferenceRemovedResultCompatibilityApis()
    {
        var symbolViolations = ArchitectureSources.RepositoryCSharp()
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<SimpleNameSyntax>()
                .Where(name => LegacyResultCompatibilitySymbols.Contains(
                    name.Identifier.ValueText))
                .Select(name => ArchitectureSources.Describe(
                    source,
                    name,
                    $"removed Result compatibility symbol '{name.Identifier.ValueText}' is forbidden")));
        var singleGenericResultViolations = ArchitectureSources.RepositoryCSharp()
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<GenericNameSyntax>()
                .Where(name => name.Identifier.ValueText == "Result" &&
                               name.TypeArgumentList.Arguments.Count == 1)
                .Select(name => ArchitectureSources.Describe(
                    source,
                    name,
                    "legacy Result<T> is forbidden; use Result<T, TError> or a closed outcome union")));

        AssertNoViolations(symbolViolations
            .Concat(singleGenericResultViolations)
            .ToArray());
    }

    [Fact]
    public async Task MongoApplicationBoundaryAnalyzer_ReportsRawMongoQueryTypesOutsidePersistence()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using MongoDB.Driver;

            public sealed class Example(IMongoCollection<string> collection)
            {
                public FilterDefinition<string> FindAll() => Builders<string>.Filter.Empty;
            }
            """,
            "/src/features/Example/Example.Application/Endpoints/Example.cs");

        Assert.Equal(3, diagnostics.Count(diagnostic =>
            diagnostic.Id == MongoApplicationBoundaryAnalyzer.DiagnosticId));
    }

    [Fact]
    public async Task MongoApplicationBoundaryAnalyzer_AllowsPersistenceAndMigrationSources()
    {
        var diagnostics = await AnalyzeAsync(
            """
            using MongoDB.Driver;

            public sealed class Example(IMongoCollection<string> collection)
            {
                public FilterDefinition<string> FindAll() => Builders<string>.Filter.Empty;
            }
            """,
            "/src/features/Example/Example.Application/Persistence/ExampleFilters.cs");

        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Id == MongoApplicationBoundaryAnalyzer.DiagnosticId);
    }

    [Fact]
    public void RuntimeMongoFilters_AreDefinedByNamedFilterHelpers()
    {
        var violations = ArchitectureSources.RuntimeMongoPersistenceCSharp()
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(IsMongoFilterBuilder)
                .Where(filter => !filter.AncestorsAndSelf()
                    .OfType<ClassDeclarationSyntax>()
                    .Any(@class => @class.Identifier.ValueText.EndsWith(
                        "Filters",
                        StringComparison.Ordinal)))
                .Select(filter => ArchitectureSources.Describe(
                    source,
                    filter,
                    "Mongo filter must be defined in a named *Filters helper")))
            .ToArray();

        AssertNoViolations(violations);
    }

    [Fact]
    public void ServerComposition_UsesFeatureRegistrationEntryPoints()
    {
        var program = ArchitectureSources.ServerProgram();
        var invokedMethods = program.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(GetInvokedMethodName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        var missingEntryPoints = FeatureRegistrationMethods
            .Where(method => !invokedMethods.Contains(method))
            .Select(method =>
                $"{program.RelativePath}: feature registration '{method}' is not composed by Pole.Server")
            .ToArray();

        AssertNoViolations(missingEntryPoints);

        var mongoImplementationReferences = program.Root.DescendantNodes()
            .OfType<SimpleNameSyntax>()
            .Where(name => name.Identifier.ValueText.StartsWith("Mongo", StringComparison.Ordinal))
            .Select(name => ArchitectureSources.Describe(
                program,
                name,
                "Pole.Server must not reference a feature Mongo implementation directly"))
            .ToArray();

        AssertNoViolations(mongoImplementationReferences);
    }

    private static bool IsMongoFilterBuilder(MemberAccessExpressionSyntax memberAccess) =>
        memberAccess.Name.Identifier.ValueText == "Filter" &&
        memberAccess.Expression.DescendantNodesAndSelf()
            .OfType<SimpleNameSyntax>()
            .Any(name => name.Identifier.ValueText == "Builders");

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax { Name: SimpleNameSyntax name } => name.Identifier.ValueText,
            SimpleNameSyntax name => name.Identifier.ValueText,
            _ => null
        };

    private static async Task<IReadOnlyCollection<Diagnostic>> AnalyzeAsync(
        string source,
        string filePath)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath);
        var compilation = CSharpCompilation.Create(
            "ArchitectureAnalyzerTests",
            [syntaxTree]);
        var compilationWithAnalyzers = compilation.WithAnalyzers(
            [new MongoApplicationBoundaryAnalyzer()]);

        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    private static void AssertNoViolations(IReadOnlyCollection<string> violations) =>
        Assert.True(
            violations.Count == 0,
            $"Architecture boundary violations:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
}
