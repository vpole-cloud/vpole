using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class TypedMongoFieldFilterAnalyzerTests
{
    [Theory]
    [InlineData("Feature.Application/Mongo/ItemFilters.cs", true)]
    [InlineData("Feature.Application/Runs/Mongo/ItemFilters.cs", true)]
    [InlineData("Feature.Application/Persistence/ItemFilters.cs", true)]
    [InlineData("Feature.Application/Runs/Handler.cs", false)]
    [InlineData("Feature.Migrations/Indexes.cs", false)]
    [InlineData("shared/Database/FilterExtensions.cs", false)]
    public async Task Raw_literal_fields_are_rejected_only_at_the_runtime_boundary(string path, bool rejected)
    {
        var diagnostics = await Analyze(path, "builder.Eq(\"State.WorkerId\", \"worker\")");
        Assert.Equal(rejected ? 1 : 0, diagnostics.Length);
    }

    [Fact]
    public async Task Typed_field_expression_is_allowed()
    {
        Assert.Empty(await Analyze("Feature.Application/Runs/Mongo/ItemFilters.cs",
            "builder.Eq(item => item.WorkerId, \"worker\")"));
    }

    private static async Task<Diagnostic[]> Analyze(string path, string expression)
    {
        var source = $$"""
            using System;
            using System.Linq.Expressions;
            namespace MongoDB.Driver
            {
                public class FilterDefinitionBuilder<T>
                {
                    public bool Eq(string field, string value) => true;
                    public bool Eq(Expression<Func<T, string>> field, string value) => true;
                }
            }
            public sealed record Item(string WorkerId);
            public static class ItemFilters
            {
                public static bool Matches(MongoDB.Driver.FilterDefinitionBuilder<Item> builder) => {{expression}};
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(value => MetadataReference.CreateFromFile(value));
        var compilation = CSharpCompilation.Create("TypedFilterRule",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        return (await compilation.WithAnalyzers([new TypedMongoFieldFilterAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()).ToArray();
    }
}
