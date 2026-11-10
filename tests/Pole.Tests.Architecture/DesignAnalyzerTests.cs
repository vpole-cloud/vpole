using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class DesignAnalyzerTests
{
    private const string EndpointPath = "/src/features/Sample/Sample.Application/Endpoints/Create.cs";
    private const string DomainPath = "/src/features/Sample/Sample.Domain/Widget.cs";

    private const string Mediator = """
                                    namespace Pole.Mediator;
                                    public interface IMediatorOutcomeHandler<TRequest, TOutcome> { }
                                    """;

    private const string Domain = """
                                  namespace Pole.Features.Sample.Domain;
                                  public sealed record Widget(int Value);
                                  public static class WidgetFactory
                                  {
                                      public static Widget Create(int value) => new(value);
                                  }
                                  """;

    [Fact]
    public async Task EndpointOutcome_AllowsRealPreviewUnionDeclaredWithHandler()
    {
        var source = """
                     public record Created;
                     public record Rejected;
                     public union CreateOutcome(Created, Rejected);
                     public sealed class Handler : global::Pole.Mediator.IMediatorOutcomeHandler<int, CreateOutcome>;
                     """;
        Assert.Empty(
            await Analyze(new EndpointOutcomeOwnershipAnalyzer(), (Mediator, "Mediator.cs"), (source, EndpointPath)));
    }

    [Fact]
    public async Task EndpointOutcome_RejectsSharedUnionFromAnotherFile()
    {
        var diagnostics = await Analyze(
            new EndpointOutcomeOwnershipAnalyzer(),
            (Mediator, "Mediator.cs"),
            ("public record Done; public union SharedOutcome(Done);", "Common/Outcomes.cs"),
            ("public class Handler : Pole.Mediator.IMediatorOutcomeHandler<int, SharedOutcome>;", EndpointPath));
        Assert.Equal(EndpointOutcomeOwnershipAnalyzer.DiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Theory]
    [InlineData("FirstOutcome")]
    [InlineData("SecondOutcome")]
    public async Task EndpointOutcome_RejectsMultipleHandlersEvenWhenOutcomesAreColocated(string secondOutcome)
    {
        var source = $$"""
                       public record Done;
                       public union FirstOutcome(Done);
                       public union SecondOutcome(Done);
                       public sealed class First : Pole.Mediator.IMediatorOutcomeHandler<int, FirstOutcome>;
                       public sealed class Second : Pole.Mediator.IMediatorOutcomeHandler<int, {{secondOutcome}}>;
                       """;
        var diagnostics = await Analyze(
            new EndpointOutcomeOwnershipAnalyzer(),
            (Mediator, "Mediator.cs"),
            (source, EndpointPath));
        Assert.Equal(2, diagnostics.Length);
        Assert.All(
            diagnostics,
            diagnostic => Assert.Equal(EndpointOutcomeOwnershipAnalyzer.SingleHandlerDiagnosticId, diagnostic.Id));
    }

    [Fact]
    public async Task EndpointOutcome_RejectsOneHandlerImplementingMultipleRequestContracts()
    {
        var source = """
                     public record Done;
                     public union Outcome(Done);
                     public sealed class Handler :
                         Pole.Mediator.IMediatorOutcomeHandler<int, Outcome>,
                         Pole.Mediator.IMediatorOutcomeHandler<string, Outcome>;
                     """;
        var diagnostics = await Analyze(
            new EndpointOutcomeOwnershipAnalyzer(),
            (Mediator, "Mediator.cs"),
            (source, EndpointPath));
        Assert.Equal(EndpointOutcomeOwnershipAnalyzer.SingleHandlerDiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task EndpointOutcome_DoesNotConfuseUnrelatedInterfaceOrBackgroundHandler() => Assert.Empty(
        await Analyze(
            new EndpointOutcomeOwnershipAnalyzer(),
            (Mediator, "Mediator.cs"),
            ("public record Done; public union SharedOutcome(Done);", "Common/Outcomes.cs"),
            ("public class Background : Pole.Mediator.IMediatorOutcomeHandler<int, SharedOutcome>;",
                "Jobs/Background.cs"),
            ("""
             public interface IMediatorOutcomeHandler<TRequest, TOutcome>;
             public class Handler : IMediatorOutcomeHandler<int, SharedOutcome>;
             """, EndpointPath)));

    [Theory]
    [InlineData("new global::Pole.Features.Sample.Domain.Widget(1)")]
    [InlineData("new(1)")]
    [InlineData("new Alias(1)")]
    public async Task Factory_RejectsExplicitQualifiedAliasedAndTargetTypedConstruction(string expression)
    {
        var source = $$"""
                       using Alias = Pole.Features.Sample.Domain.Widget;
                       public static class Consumer
                       {
                           public static Alias Make() => {{expression}};
                       }
                       """;
        var diagnostics = await Analyze(
            new DomainDesignAnalyzer(),
            (Domain, DomainPath),
            (source, "Tests/Consumer.cs"));
        Assert.Equal(DomainDesignAnalyzer.FactoryDiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task Factory_AllowsFactoryCallsOwnTransitionsAndUnrelatedNames() => Assert.Empty(
        await Analyze(
            new DomainDesignAnalyzer(),
            ("""
             namespace Pole.Features.Sample.Domain;
             public sealed record Widget(int Value)
             {
                 public Widget Increment() => new(Value + 1);
             }
             public static class WidgetFactory
             {
                 public static Widget Create(int value) => new(value);
             }
             """, DomainPath),
            ("""
             public record Widget(int Value);
             public static class Consumer
             {
                 public static Widget Make() => new(1);
                 public static Pole.Features.Sample.Domain.Widget MakeDomain() =>
                     Pole.Features.Sample.Domain.WidgetFactory.Create(1);
             }
             """, "Consumer.cs")));

    [Fact]
    public async Task Factory_DoesNotTreatUnrelatedFactoryReturnTypeAsEntityOwnership() => Assert.Empty(
        await Analyze(
            new DomainDesignAnalyzer(),
            ("""
             namespace Pole.Features.Sample.Domain;
             public record Widget(int Value);
             public static class WidgetFactory
             {
                 public static string Create() => "transport value";
             }
             public static class Consumer
             {
                 public static Widget Make() => new(1);
             }
             """, DomainPath)));

    [Theory]
    [InlineData("int[]")]
    [InlineData("global::System.Collections.Generic.List<int>")]
    [InlineData("System.Collections.Generic.IDictionary<string, int>")]
    public async Task Collections_RejectMutableEntityProperties(string type)
    {
        var diagnostics = await Analyze(new DomainDesignAnalyzer(), (EntityWithProperty(type, "Values"), DomainPath));
        Assert.Equal(DomainDesignAnalyzer.CollectionDiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Theory]
    [InlineData("System.Collections.Immutable.ImmutableArray<int>")]
    [InlineData("System.Collections.Immutable.ImmutableList<int>")]
    [InlineData("System.Collections.Generic.IReadOnlyList<int>")]
    public async Task Collections_AllowImmutableAndReadOnlyEntityApis(string type) => Assert.Empty(
        await Analyze(new DomainDesignAnalyzer(), (EntityWithProperty(type, "Values"), DomainPath)));

    [Fact]
    public async Task Collections_DoNotBanDtoArraysOrPrivateAccumulators() => Assert.Empty(
        await Analyze(
            new DomainDesignAnalyzer(),
            ("""
             namespace Pole.Features.Sample.Domain;
             public record RequestDto(int[] Values);
             public sealed class Widget
             {
                 private System.Collections.Generic.List<int> Values { get; } = new();
             }
             public static class WidgetFactory
             {
                 public static Widget Create() => new();
             }
             """, DomainPath)));

    [Theory]
    [InlineData("string", "State")]
    [InlineData("bool", "Status")]
    [InlineData("int?", "Lifecycle")]
    public async Task States_RejectPrimitiveDomainDiscriminators(string type, string name)
    {
        var diagnostics = await Analyze(new DomainDesignAnalyzer(), (EntityWithProperty(type, name), DomainPath));
        Assert.Equal(DomainDesignAnalyzer.StateDiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task States_AllowTypedDomainStatesAndTransportStrings() => Assert.Empty(
        await Analyze(
            new DomainDesignAnalyzer(),
            ("""
             namespace Pole.Features.Sample.Domain;
             public enum WidgetState { Ready, Failed }
             public record Widget(WidgetState State, string Description);
             """, DomainPath),
            ("namespace Pole.Features.Sample.Contracts; public record Response(string State);", "Response.cs")));

    [Fact]
    public async Task MongoRule_DoesNotApplyOutsideApplicationProjects() => Assert.Empty(
        await Analyze(
            new MongoApplicationBoundaryAnalyzer(),
            ("public class Builders; public record Fixture(Builders Value);", "Tests/Fixture.cs")));

    [Theory]
    [InlineData("public record Pair(int First, int Second);")]
    [InlineData("public record struct Pair(int First, int Second);")]
    [InlineData("public record Pair(\n    int First, int Second);")]
    [InlineData("public record Pair(int First,\n    int Second);")]
    public async Task Layout_RejectsCompressedPositionalRecords(string source)
    {
        var diagnostics = await Analyze(new PositionalDeclarationLayoutAnalyzer(), (source, "Pair.cs"));
        Assert.Equal(PositionalDeclarationLayoutAnalyzer.DiagnosticId, Assert.Single(diagnostics).Id);
    }

    [Theory]
    [InlineData("public record Pair(\n    int First,\n    int Second);")]
    [InlineData("public record struct Pair(\n    int First,\n    int Second);")]
    [InlineData("public record A; public record B; public union Pair(A, B);")]
    [InlineData("public record A; public record B; public record C; public union Triple(A, B, C);")]
    [InlineData("public delegate void Pair(int first, int second);")]
    [InlineData("public delegate void Triple(int first, int second, int third);")]
    [InlineData("public delegate void Pair(\n    int first,\n    int second);")]
    [InlineData("public record A; public record B; public union Pair(\n    A,\n    B);")]
    [InlineData("public record Single(int Value);")]
    [InlineData("public record A; public union Single(A);")]
    [InlineData("public class Pair(int first, int second);")]
    [InlineData("public class C { public void M(int first, int second) {} }")]
    [InlineData("public class @union(int first, int second);")]
    [InlineData("public record Pair(\n    [property: System.Obsolete] int First,\n    int Second);")]
    public async Task Layout_AllowsExpandedRecordsSingleParametersAndOtherDeclarations(string source) =>
        Assert.Empty(await Analyze(new PositionalDeclarationLayoutAnalyzer(), (source, "Pair.cs")));

    [Fact]
    public async Task RepositoryPositionalRecords_UseExpandedLayout()
    {
        // Also check the analyzer and architecture-test projects, which are exempt
        // from the global build-analyzer wiring.
        var compilation = CSharpCompilation.Create(
            "RepositoryLayout",
            ArchitectureSources.RepositoryCSharp().Select(source => source.SyntaxTree));
        var diagnostics = await compilation.WithAnalyzers([new PositionalDeclarationLayoutAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        Assert.True(diagnostics.IsEmpty, string.Join(Environment.NewLine, diagnostics));
    }

    private static string EntityWithProperty(string type, string name) => $$"""
                                                                            namespace Pole.Features.Sample.Domain;
                                                                            public sealed record Widget({{type}} {{name}});
                                                                            public static class WidgetFactory
                                                                            {
                                                                                public static Widget Create({{type}} value) => new(value);
                                                                            }
                                                                            """;

    private static async Task<Diagnostic[]> Analyze(
        DiagnosticAnalyzer analyzer,
        params (string Source, string Path)[] sources)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Characterization",
            sources.Select(source => CSharpSyntaxTree.ParseText(
                source.Source,
                new CSharpParseOptions(LanguageVersion.Preview),
                source.Path)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return (await compilation.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync()).ToArray();
    }
}
