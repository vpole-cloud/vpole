// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class TypedMongoFieldFilterAnalyzerTests
{
    [Theory]
    [InlineData("Feature.Application/Mongo/ItemFilters.cs")]
    [InlineData("Feature.Application/Runs/Handler.cs")]
    [InlineData("Feature.Application/Endpoints/Handler.cs")]
    [InlineData("Feature.Application/Consumers/Consumer.cs")]
    [InlineData("Feature.Application/Jobs/Job.cs")]
    [InlineData("shared/Database/FilterExtensions.cs")]
    [InlineData("Feature.Migrations/ActuallyProduction.cs")]
    public async Task Production_string_fields_are_rejected_independently_of_folder(string path) =>
        Assert.Single(await Analyze("builder.Eq(\"State.WorkerId\", \"worker\")", path: path));

    [Theory]
    [InlineData("Migration", false)]
    [InlineData("TestSupport", false)]
    [InlineData("Production", true)]
    public async Task Migration_and_test_project_roles_remain_exempt(string role, bool test) => Assert.Empty(
        await Analyze("builder.Eq(\"State.WorkerId\", \"worker\")", role: role, test: test));

    [Theory]
    [InlineData("builder.Eq(StateField, \"worker\")")]
    [InlineData("builder.Eq(localField, \"worker\")")]
    [InlineData("builder.Eq(nameof(Item.WorkerId), \"worker\")")]
    [InlineData("builder.Eq(GetField(), \"worker\")")]
    public async Task Constants_variables_and_computed_string_fields_are_rejected(string expression) =>
        Assert.Single(await Analyze(expression));

    [Theory]
    [InlineData("UpdateDefinitionBuilder", "builder.Set(localField, \"worker\")")]
    [InlineData("SortDefinitionBuilder", "builder.Ascending(localField)")]
    [InlineData("IndexKeysDefinitionBuilder", "builder.Ascending(localField)")]
    public async Task Other_field_builders_require_the_same_typed_contract(string builder, string expression) =>
        Assert.Single(await Analyze(expression, builder: builder));

    [Theory]
    [InlineData("FilterDefinitionBuilder", "builder.Eq(item => item.WorkerId, \"worker\")")]
    [InlineData("UpdateDefinitionBuilder", "builder.Set(item => item.WorkerId, \"worker\")")]
    [InlineData("SortDefinitionBuilder", "builder.Ascending(item => item.WorkerId)")]
    [InlineData("IndexKeysDefinitionBuilder", "builder.Ascending(item => item.WorkerId)")]
    [InlineData("FilterDefinitionBuilder", "builder.Eq(new MongoDB.Driver.FieldDefinition<Item>(), \"worker\")")]
    public async Task Typed_fields_are_allowed(string builder, string expression) =>
        Assert.Empty(await Analyze(expression, builder: builder));

    private static async Task<Diagnostic[]> Analyze(
        string expression,
        string path = "Feature.Application/Mongo/ItemFilters.cs",
        string role = "Production",
        bool test = false,
        string builder = "FilterDefinitionBuilder")
    {
        var source = $$"""
                       using System;
                       using System.Linq.Expressions;
                       namespace MongoDB.Driver
                       {
                           public class FieldDefinition<T>
                           {
                               public static implicit operator FieldDefinition<T>(string field) => new();
                           }
                           public class FilterDefinitionBuilder<T>
                           {
                               public bool Eq(FieldDefinition<T> field, string value) => true;
                               public bool Eq(Expression<Func<T, string>> field, string value) => true;
                           }
                           public class UpdateDefinitionBuilder<T>
                           {
                               public bool Set(FieldDefinition<T> field, string value) => true;
                               public bool Set(Expression<Func<T, string>> field, string value) => true;
                           }
                           public class SortDefinitionBuilder<T>
                           {
                               public bool Ascending(FieldDefinition<T> field) => true;
                               public bool Ascending(Expression<Func<T, string>> field) => true;
                           }
                           public class IndexKeysDefinitionBuilder<T>
                           {
                               public bool Ascending(FieldDefinition<T> field) => true;
                               public bool Ascending(Expression<Func<T, string>> field) => true;
                           }
                       }
                       public sealed record Item(string WorkerId);
                       public static class ItemFilters
                       {
                           private const string StateField = "State.WorkerId";
                           private static string GetField() => StateField;
                           public static bool Matches(MongoDB.Driver.{{builder}}<Item> builder)
                           {
                               var localField = StateField;
                               return {{expression}};
                           }
                       }
                       """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(value => MetadataReference.CreateFromFile(value));
        var compilation = CSharpCompilation.Create(
            "TypedFieldRule",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        var options = new AnalyzerOptions([], new ProjectOptions(role, test));
        return (await compilation.WithAnalyzers([new TypedMongoFieldFilterAnalyzer()], options)
            .GetAnalyzerDiagnosticsAsync()).ToArray();
    }

    private sealed class ProjectOptions(
        string role,
        bool test
    ) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Values(role, test);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class Values(
        string role,
        bool test
    ) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = key switch
            {
                "build_property.PoleArchitectureRole" => role,
                "build_property.IsTestProject" => test.ToString(),
                _ => "",
            };
            return value.Length != 0;
        }
    }
}
