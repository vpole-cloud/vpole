// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class ClosedOutcomeCompilerTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("public sealed record Failed : State;", true)]
    public void ClosedStateSwitch_RequiresNewVariantToBeHandled(
        string additionalVariant,
        bool expectsNonExhaustiveWarning)
    {
        var diagnostics = Compile(
            $$"""
              public closed record State;
              public sealed record Pending : State;
              public sealed record Complete : State;
              {{additionalVariant}}
              public static class Mapping
              {
                  public static int Map(State state) => state switch
                  {
                      Pending => 0,
                      Complete => 1
                  };
              }
              """);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(expectsNonExhaustiveWarning, diagnostics.Any(diagnostic => diagnostic.Id == "CS8509"));
    }

    [Fact]
    public void OperationUnion_RejectsFailureThatOnlyAnotherOperationCanReturn()
    {
        var diagnostics = Compile(
            """
            public sealed record Found;
            public sealed record Deleted;
            public sealed record Missing;
            public sealed record InUse;
            public union ReadOutcome(Found, Missing);
            public union DeleteOutcome(Deleted, Missing, InUse);
            public static class Operations
            {
                public static ReadOutcome Read() => new InUse();
                public static DeleteOutcome Delete() => new InUse();
            }
            """);
        var error = Assert.Single(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal("CS0029", error.Id);
        Assert.Contains("ReadOutcome", error.GetMessage(), StringComparison.Ordinal);
    }

    private static IReadOnlyList<Diagnostic> Compile(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
                "ClosedOutcomeCharacterization",
                [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics();
    }
}
