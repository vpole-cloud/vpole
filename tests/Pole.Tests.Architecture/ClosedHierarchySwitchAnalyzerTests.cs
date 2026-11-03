using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class ClosedHierarchySwitchAnalyzerTests
{
    [Theory]
    [InlineData("state switch { Ready => 1, _ => 0 }")]
    [InlineData("state switch { Ready => 1, var other => 0 }")]
    [InlineData("state switch { Ready => 1, State => 0 }")]
    [InlineData("state switch { Ready => 1, global::State => 0 }")]
    [InlineData("state switch { Ready => 1, State other => 0 }")]
    [InlineData("state switch { Ready => 1, object other => 0 }")]
    [InlineData("state switch { Ready => 1, { } => 0 }")]
    [InlineData("state switch { Ready => 1, not null => 0 }")]
    public async Task Catch_all_does_not_hide_future_closed_variants(string expression)
    {
        var diagnostic = Assert.Single(await Analyze($"return {expression};"));
        Assert.Equal(ClosedHierarchySwitchAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Fact]
    public async Task Default_statement_is_rejected_for_closed_state()
    {
        Assert.Single(await Analyze("switch (state) { case Ready: return 1; default: return 0; }"));
    }

    [Fact]
    public async Task Explicit_rejected_variants_and_nullable_input_are_allowed()
    {
        Assert.Empty(await Analyze("return state switch { Ready => 1, Failed => 0, null => -1 };"));
    }

    [Fact]
    public async Task Open_extension_boundary_retains_its_defensive_fallback()
    {
        Assert.Empty(await Analyze("return state switch { Ready => 1, _ => 0 };", closed: false));
    }

    [Fact]
    public async Task Referenced_closed_hierarchy_remains_protected_after_emission()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var parsing = new CSharpParseOptions(LanguageVersion.Preview);
        var definitions = CSharpCompilation.Create("ClosedStates",
            [CSharpSyntaxTree.ParseText("""
                public closed record State;
                public sealed record Ready : State;
                public sealed record Failed : State;
                """, parsing)], references, options);
        using var assembly = new MemoryStream();
        var emitted = definitions.Emit(assembly);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var consumer = CSharpCompilation.Create("Consumer",
            [CSharpSyntaxTree.ParseText("""
                public static class Mapping
                {
                    public static int Map(State state) => state switch { Ready => 1, _ => 0 };
                }
                """, parsing)], references.Append(MetadataReference.CreateFromImage(assembly.ToArray())), options);
        var diagnostics = await consumer.WithAnalyzers([new ClosedHierarchySwitchAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task Tuple_of_closed_states_cannot_hide_a_new_variant()
    {
        Assert.Single(await Analyze("return (state, state) switch { (Ready, Ready) => 1, _ => 0 };"));
    }

    [Theory]
    [InlineData("return (state, state) switch { (Ready, Ready) => 1, (_, _) => 0 };")]
    [InlineData("return (state, state) switch { (Ready, Ready) => 1, (_, Failed) => 0 };")]
    public async Task Tuple_component_patterns_cannot_absorb_future_variants(string body)
    {
        Assert.Single(await Analyze(body));
    }

    [Fact]
    public async Task Explicit_subtype_property_patterns_remain_allowed()
    {
        Assert.Empty(await Analyze("return state switch { Ready { } => 1, Failed => 0, null => -1 };"));
        Assert.Empty(await Analyze("return state switch { State and Ready => 1, Failed => 0, null => -1 };"));
    }

    private static async Task<Diagnostic[]> Analyze(string body, bool closed = true)
    {
        var source = $$"""
            public {{(closed ? "closed" : "abstract")}} record State;
            public sealed record Ready : State;
            public sealed record Failed : State;
            public static class Mapping
            {
                public static int Map(State? state) { {{body}} }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ClosedSwitchRule",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        return (await compilation.WithAnalyzers([new ClosedHierarchySwitchAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()).ToArray();
    }
}
