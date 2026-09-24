using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Text.RegularExpressions;
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

    private static readonly string[] RawMongoQuerySymbols =
    [
        "IMongoCollection",
        "IAsyncCursor",
        "IFindFluent",
        "FilterDefinition",
        "UpdateDefinition",
        "Builders"
    ];

    private static readonly string[] ServerFeatureImplementationSymbols =
    [
        "PostgresSettings",
        "QuotaSettings",
        "OutboxProcessorJob",
        "SourceBuildQueueRecoveryJob",
        "RuntimeCellRecoveryJob",
        "ResourceDeploymentRecoveryJob",
        "RegistryProvisioningCleanupService",
        "RedisQuotaProfile",
        "FunctionAppQuotaProfile",
        "WebServiceQuotaProfile",
        "PostgresQuotaProfile"
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

    [Theory]
    [InlineData("Users")]
    [InlineData("Tenants")]
    [InlineData("StorageProfiles")]
    [InlineData("Quotas")]
    public void MigratedFeatures_UseNamedOutcomesAndDirectEndpointMappings(string feature)
    {
        var sources = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.StartsWith($"src/features/{feature}/", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(sources);
        var violations = sources.SelectMany(source => source.Root.DescendantNodes()
            .OfType<SimpleNameSyntax>()
            .Where(name =>
                name is GenericNameSyntax && name.Identifier.ValueText is "Result" or "Ok" or "Err" ||
                source.RelativePath.Contains("/Endpoints/", StringComparison.Ordinal) &&
                name.Identifier.ValueText == "IHttpProblemMapper")
            .Select(name => ArchitectureSources.Describe(source, name,
                "migrated features use named outcomes and direct endpoint HTTP mappings")))
            .ToArray();
        AssertNoViolations(violations);
    }

    [Theory]
    [InlineData("ComputeProfiles")]
    [InlineData("Domains")]
    [InlineData("RedisServices")]
    public void FeatureContractsAndEndpoints_UseNamedOutcomesAndDirectMappings(string feature)
    {
        var sources = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.StartsWith(
                $"src/features/{feature}/", StringComparison.Ordinal))
            .Where(source => source.RelativePath.Contains($"/{feature}.Contracts/", StringComparison.Ordinal) ||
                source.RelativePath.Contains("/Endpoints/", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(sources);
        var violations = sources.SelectMany(source => source.Root.DescendantNodes()
            .OfType<SimpleNameSyntax>()
            .Where(name =>
                name is GenericNameSyntax && name.Identifier.ValueText is "Result" or "Ok" or "Err" ||
                name.Identifier.ValueText == "IHttpProblemMapper")
            .Select(name => ArchitectureSources.Describe(source, name,
                "feature contracts and endpoints use named outcomes and direct HTTP mappings")))
            .ToArray();
        AssertNoViolations(violations);
    }

    [Fact]
    public void RedisApplication_UsesNamedOutcomes()
    {
        var sources = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.StartsWith(
                "src/features/RedisServices/RedisServices.Application/",
                StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(sources);
        var violations = sources.SelectMany(source => source.Root.DescendantNodes()
            .OfType<GenericNameSyntax>()
            .Where(name => name.Identifier.ValueText is "Result" or "Ok" or "Err")
            .Select(name => ArchitectureSources.Describe(source, name,
                "Redis application uses named outcomes")))
            .ToArray();
        AssertNoViolations(violations);
    }

    [Fact]
    public void ProductionSources_DoNotReferenceRemovedMongoContexts()
    {
        var csharpViolations = ArchitectureSources.ProductionCSharp()
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<SimpleNameSyntax>()
                .Where(name => LegacyMongoContextSymbols.Contains(name.Identifier.ValueText))
                .Select(name => ArchitectureSources.Describe(
                    source,
                    name,
                    $"removed persistence symbol '{name.Identifier.ValueText}' is forbidden")))
            .ToArray();
        var fsharpViolations = FindFSharpSymbols(
            ArchitectureSources.ProductionFSharp(),
            LegacyMongoContextSymbols,
            symbol => $"removed persistence symbol '{symbol}' is forbidden");

        AssertNoViolations(csharpViolations.Concat(fsharpViolations).ToArray());
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

        var fsharpSymbolViolations = FindFSharpSymbols(
            ArchitectureSources.RepositoryFSharp(),
            LegacyResultCompatibilitySymbols,
            symbol => $"removed Result compatibility symbol '{symbol}' is forbidden");
        var fsharpSingleGenericResultViolations = ArchitectureSources.RepositoryFSharp()
            .SelectMany(source => source.Lines
                .Where(line => Regex.IsMatch(
                    line.Text,
                    @"\bResult\s*<\s*[^,>\r\n]+\s*>",
                    RegexOptions.CultureInvariant))
                .Select(line => ArchitectureSources.Describe(
                    source,
                    line.Number,
                    "legacy Result<T> is forbidden; use Result<T, TError> or a closed outcome union")));

        AssertNoViolations(symbolViolations
            .Concat(singleGenericResultViolations)
            .Concat(fsharpSymbolViolations)
            .Concat(fsharpSingleGenericResultViolations)
            .ToArray());
    }

    [Fact]
    public void FSharpApplicationSources_KeepRawMongoQueriesInsidePersistenceOrMigrations()
    {
        var applicationSources = ArchitectureSources.ProductionFSharp()
            .Where(source => source.RelativePath.Contains(
                ".Application/",
                StringComparison.Ordinal));
        var violations = applicationSources
            .Where(source =>
                !source.RelativePath.Contains("/Persistence/", StringComparison.Ordinal) &&
                !source.RelativePath.Contains("/Migrations/", StringComparison.Ordinal))
            .SelectMany(source => FindFSharpSymbols(
                [source],
                RawMongoQuerySymbols,
                symbol => $"raw Mongo query symbol '{symbol}' must only be used by a persistence capability"))
            .ToArray();

        AssertNoViolations(violations);
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
    public void MediatorEndpointHandlers_UseClosedOutcomeTypes()
    {
        var csharpViolations = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.Contains(
                ".Application/Endpoints/",
                StringComparison.Ordinal))
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<GenericNameSyntax>()
                .Where(name => name.Identifier.ValueText == "IMediatorOutcomeHandler")
                .Select(name => (Handler: name, Outcome: name.TypeArgumentList.Arguments.Last()))
                .Where(item => !GetTypeName(item.Outcome).EndsWith(
                    "Outcome",
                    StringComparison.Ordinal))
                .Select(name => ArchitectureSources.Describe(
                    source,
                    name.Handler,
                    $"mediator endpoint handler outcome '{GetTypeName(name.Outcome)}' must be a closed *Outcome type")));
        var fsharpViolations = ArchitectureSources.ProductionFSharp()
            .Where(source => source.RelativePath.Contains(
                ".Application/Endpoints/",
                StringComparison.Ordinal))
            .SelectMany(source => source.Lines
                .Select(line => (
                    Line: line,
                    Match: Regex.Match(
                        line.Text,
                        @"\bIMediatorOutcomeHandler\s*<[^,]+,\s*([A-Za-z0-9_.']+)\s*>",
                        RegexOptions.CultureInvariant)))
                .Where(item => item.Match.Success &&
                               !item.Match.Groups[1].Value.EndsWith(
                                   "Outcome",
                                   StringComparison.Ordinal))
                .Select(item => ArchitectureSources.Describe(
                    source,
                    item.Line.Number,
                    $"mediator endpoint handler outcome '{item.Match.Groups[1].Value}' must be a closed *Outcome type")));

        AssertNoViolations(csharpViolations.Concat(fsharpViolations).ToArray());
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

        var fsharpViolations = ArchitectureSources.RuntimeMongoPersistenceFSharp()
            .Where(source => !Path.GetFileNameWithoutExtension(source.RelativePath)
                .EndsWith("Filters", StringComparison.Ordinal))
            .SelectMany(source => source.Lines
                .Where(line => Regex.IsMatch(
                    line.Text,
                    @"\bBuilders\s*<[^>]+>\s*\.\s*Filter\b",
                    RegexOptions.CultureInvariant))
                .Select(line => ArchitectureSources.Describe(
                    source,
                    line.Number,
                    "Mongo filter must be defined in a named *Filters helper")));

        AssertNoViolations(violations.Concat(fsharpViolations).ToArray());
    }

    [Fact]
    public void ServerComposition_UsesFeatureRegistrationEntryPoints()
    {
        var program = ArchitectureSources.ServerProgram();
        Assert.Contains(program.Root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => GetInvokedMethodName(invocation) == "AddPolePlatform");
        var hosting = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(
            Path.Combine(ArchitectureSources.RepositoryRoot, "src/api/Pole.Platform.Hosting/PolePlatform.cs")));
        var invokedMethods = program.Root.DescendantNodes().Concat(hosting.GetRoot().DescendantNodes())
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

    [Fact]
    public void ServerComposition_DoesNotOwnFeatureImplementationDetails()
    {
        var sources = ArchitectureSources.ServerCSharp().ToArray();
        var symbolViolations = sources
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<SimpleNameSyntax>()
                .Where(name => ServerFeatureImplementationSymbols.Contains(
                    name.Identifier.ValueText,
                    StringComparer.Ordinal))
                .Select(name => ArchitectureSources.Describe(
                    source,
                    name,
                    $"feature implementation symbol '{name.Identifier.ValueText}' belongs in its feature registration")));
        var recurringJobViolations = sources
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<SimpleNameSyntax>()
                .Where(name => name.Identifier.ValueText == "RecurringJob")
                .Select(name => ArchitectureSources.Describe(
                    source,
                    name,
                    "feature recurring jobs must be scheduled by their owning feature")));

        AssertNoViolations(symbolViolations.Concat(recurringJobViolations).ToArray());
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

    private static string GetTypeName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        QualifiedNameSyntax qualified => GetTypeName(qualified.Right),
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => type.ToString()
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

    private static IEnumerable<string> FindFSharpSymbols(
        IEnumerable<ArchitectureTextSource> sources,
        IEnumerable<string> symbols,
        Func<string, string> message)
    {
        var symbolPatterns = symbols
            .Select(symbol => (
                Symbol: symbol,
                Pattern: new Regex(
                    $@"\b{Regex.Escape(symbol)}\b",
                    RegexOptions.CultureInvariant)))
            .ToArray();

        return sources.SelectMany(source => source.Lines.SelectMany(line =>
            symbolPatterns
                .Where(item => item.Pattern.IsMatch(line.Text))
                .Select(item => ArchitectureSources.Describe(
                    source,
                    line.Number,
                    message(item.Symbol)))));
    }

    private static void AssertNoViolations(IReadOnlyCollection<string> violations) =>
        Assert.True(
            violations.Count == 0,
            $"Architecture boundary violations:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
}
