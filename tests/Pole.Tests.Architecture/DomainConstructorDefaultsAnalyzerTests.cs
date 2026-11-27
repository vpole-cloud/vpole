// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class DomainConstructorDefaultsAnalyzerTests
{
    [Theory]
    [InlineData("public sealed record Model(long Revision = 1);")]
    [InlineData("public sealed class Model(long revision = 1) { }")]
    [InlineData("public sealed class Model { public Model(long revision = 1) { } }")]
    [InlineData("public static class Factory { public sealed record CreateOptions(bool Enabled = true); }")]
    public async Task Domain_constructor_defaults_are_rejected_without_exceptions(string declaration)
    {
        var diagnostic = Assert.Single(await Analyze("Pole.Features.Sample.Domain", declaration));

        Assert.Equal(DomainConstructorDefaultsAnalyzer.DiagnosticId, diagnostic.Id);
    }

    [Theory]
    [InlineData("public sealed record Model(long Revision);")]
    [InlineData("public sealed class Model { public Model(long revision) { } }")]
    [InlineData("public static class Factory { public static int Create(int revision = 1) => revision; }")]
    public async Task Explicit_constructors_and_factory_method_defaults_are_allowed(string declaration) =>
        Assert.Empty(await Analyze("Pole.Features.Sample.Domain", declaration));

    [Fact]
    public async Task Non_domain_constructor_defaults_are_outside_this_rule() => Assert.Empty(
        await Analyze("Pole.Features.Sample.Application", "public sealed record Request(int Limit = 10);"));

    private static async Task<Diagnostic[]> Analyze(string ns, string declaration)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Sample",
            [
                CSharpSyntaxTree.ParseText(
                    $"namespace {ns}; {declaration}",
                    new CSharpParseOptions(LanguageVersion.Preview)),
            ],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        return (await compilation.WithAnalyzers([new DomainConstructorDefaultsAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()).ToArray();
    }
}
