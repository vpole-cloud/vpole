using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class PersistedFeatureModelAnalyzerTests
{
    private const string Driver = """
        namespace MongoDB.Driver
        {
            public interface IMongoCollection<T> { }
            public interface IMongoDatabase
            {
                IMongoCollection<T> GetCollection<T>(string name);
            }
            public sealed class Store<T>
            {
                private IMongoCollection<T>? collection;
            }
        }
        """;

    [Theory]
    [InlineData("Pole.Features.Sample.Application", "Pole.Features.Sample.Application")]
    [InlineData("Pole.Features.Sample.Contracts", "Pole.Features.Sample.Contracts")]
    [InlineData("Pole.Features.Sample.Domain", "Pole.Features.Sample.Application")]
    [InlineData("Pole.Features.Sample.Domainish", "Pole.Features.Sample.Domain")]
    public async Task Feature_owned_root_models_require_both_domain_namespace_and_project(
        string modelNamespace,
        string assembly)
    {
        var diagnostics = await Analyze(modelNamespace, assembly, false);

        Assert.Equal(PersistedFeatureModelAnalyzer.DiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task GetCollection_is_checked_when_no_collection_field_is_declared()
    {
        var diagnostics = await Analyze("Pole.Features.Sample.Application", "Pole.Features.Sample.Application", true);

        Assert.Equal(PersistedFeatureModelAnalyzer.DiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task Domain_models_are_allowed_in_typed_collection_apis()
    {
        Assert.Empty(await Analyze("Pole.Features.Sample.Domain", "Pole.Features.Sample.Domain", false));
        Assert.Empty(await Analyze("Pole.Features.Sample.Domain", "Pole.Features.Sample.Domain", true));
    }

    [Fact]
    public async Task Technical_documents_are_not_feature_models()
    {
        Assert.Empty(await Analyze("MongoDB.Bson", "Technical", false));
        Assert.Empty(await Analyze("MongoDB.Bson", "Technical", true));
    }

    [Fact]
    public async Task Closed_generic_persistence_wrappers_cannot_hide_application_models()
    {
        var diagnostics = await Analyze("Pole.Features.Sample.Application", "Pole.Features.Sample.Application", false,
            "private MongoDB.Driver.Store<Model>? store;");

        Assert.Equal(PersistedFeatureModelAnalyzer.DiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task Ordinary_generic_containers_are_outside_persistence_ownership()
    {
        Assert.Empty(await Analyze("Pole.Features.Sample.Application", "Pole.Features.Sample.Application", false,
            "private System.Collections.Generic.List<(Model, string)>? rows;"));
    }

    private static async Task<Diagnostic[]> Analyze(
        string modelNamespace, string assembly, bool invoke, string? customUse = null)
    {
        var use = customUse ?? (invoke
            ? "public void Read(MongoDB.Driver.IMongoDatabase db) { var rows = db.GetCollection<Model>(\"rows\"); }"
            : "private MongoDB.Driver.IMongoCollection<Model>? rows;");
        var source = $$"""
            using Model = {{modelNamespace}}.Document;
            {{Driver}}
            namespace {{modelNamespace}} { public sealed record Document(string Id); }
            namespace Persistence { public sealed class Store { {{use}} } }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(assembly,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        return (await compilation.WithAnalyzers([new PersistedFeatureModelAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()).ToArray();
    }
}
