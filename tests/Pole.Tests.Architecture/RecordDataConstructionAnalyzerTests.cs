// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class RecordDataConstructionAnalyzerTests
{
    [Theory]
    [InlineData("public record R(int Value) { public int Extra { get; init; } }")]
    [InlineData("public record struct R(int Value) { public int Extra { get; init; } }")]
    [InlineData("public record R(int Value) { private int extra; }")]
    [InlineData("public record R(int Value) { public int Extra { get => field; init; } }")]
    [InlineData("public record R(int Value) { public int Value { get; init; } = Value; }")]
    [InlineData("public partial record R(int Value); public partial record R { public int Extra { get; init; } }")]
    [InlineData("public record R(int Value) { public R() : this(0) {} }")]
    public async Task RejectsMixedStoredDataOrAlternateDataConstructors(string source)
    {
        var diagnostics = await Analyze(source);
        Assert.Equal(RecordDataConstructionAnalyzer.DiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Theory]
    [InlineData("public record R(int Value, string Name);")]
    [InlineData("public record R(int Value) { public int Double => Value * 2; }")]
    [InlineData("public record R(int Value) { public int Double { get => Value * 2; } }")]
    [InlineData("public record R(int Value) { public static string Kind { get; } = nameof(R); }")]
    [InlineData("public record R(int Value) { public const int Limit = 10; }")]
    [InlineData("public abstract record R(int Value) { public abstract int Extra { get; } }")]
    [InlineData("public record R { public int Value { get; init; } }")]
    [InlineData("public record R { public R(int value) { Value = value; } public int Value { get; init; } }")]
    [InlineData("public class Service(int value) { public int Value { get; } = value; }")]
    [InlineData("public record Base(int Value); public record R(int Value) : Base(Value);")]
    [InlineData("public record R(int Value) { protected R(R original) { Value = original.Value; } }")]
    public async Task AllowsConsistentDataComputedStaticAbstractAndDiMembers(string source) =>
        Assert.Empty(await Analyze(source));

    [Fact]
    public async Task ExactLegacyMemberDoesNotExemptAnotherMemberOfTheSameType()
    {
        var source = """
                     namespace Pole.Features.ComputeProfiles.Events;
                     public sealed record ComputeProfileCatalogChangedEvent(int Value)
                     {
                         public long EventCore { get; init; }
                         public long NewFence { get; init; }
                     }
                     """;
        var diagnostics = await Analyze(
            source,
            "src/features/ComputeProfiles/ComputeProfiles.Events/ComputeProfileCatalogChangedEvent.cs");
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("NewFence", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyMemberNameInAnotherFileIsNotExempt()
    {
        var source = """
                     namespace Pole.Features.ComputeProfiles.Events;
                     public sealed record ComputeProfileCatalogChangedEvent(int Value)
                     {
                         public long EventCore { get; init; }
                     }
                     """;
        Assert.Single(await Analyze(source, "Other/ComputeProfileCatalogChangedEvent.cs"));
    }

    [Fact]
    public void RepositoryMixedDataMembers_MatchExactLegacyRatchet()
    {
        // No full-project compilation is needed to discover record storage. This also covers
        // tests/analyzers, which are excluded from automatic production analyzer registration.
        var trees = ArchitectureSources.RepositoryCSharp().Select(source => source.SyntaxTree).ToArray();
        var compilation = CSharpCompilation.Create("RecordStorageInventory", trees, FrameworkReferences());
        var actual = trees
            .SelectMany(tree =>
                tree.GetRoot()
                    .DescendantNodes()
                    .OfType<RecordDeclarationSyntax>()
                    .Select(record => compilation.GetSemanticModel(tree).GetDeclaredSymbol(record)))
            .OfType<INamedTypeSymbol>()
            .GroupBy(type => type.ToDisplayString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .SelectMany(RecordDataConstructionAnalyzer.MixedDataMembers)
            .Select(RecordDataConstructionAnalyzer.MemberKey)
            .ToHashSet(StringComparer.Ordinal);
        var introduced = actual.Except(RecordDataConstructionLegacy.Members).Order(StringComparer.Ordinal);
        var stale = RecordDataConstructionLegacy.Members.Except(actual).Order(StringComparer.Ordinal);
        Assert.True(!introduced.Any(), "New mixed record data:\n" + string.Join("\n", introduced));
        Assert.True(!stale.Any(), "Remove corrected legacy allowances:\n" + string.Join("\n", stale));
    }

    private static IEnumerable<MetadataReference> FrameworkReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path));

    private static async Task<Diagnostic[]> Analyze(string source, string path = "Record.cs")
    {
        var compilation = CSharpCompilation.Create(
            "RecordConstruction",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path)],
            FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return (await compilation.WithAnalyzers([new RecordDataConstructionAnalyzer()]).GetAnalyzerDiagnosticsAsync())
            .ToArray();
    }
}
