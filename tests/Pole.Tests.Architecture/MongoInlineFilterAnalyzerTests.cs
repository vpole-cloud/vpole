using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class MongoInlineFilterAnalyzerTests
{
    [Theory]
    [InlineData("Queries", "collection.Find(x => x > 0);", 1, "Mongo")]
    [InlineData("Queries", "collection.Find(x => x > 0);", 1, "Runs/Mongo")]
    [InlineData("Queries", "collection.Find(x => x > 0);", 0, "Runs")]
    [InlineData("Queries", "collection.Delete(session: new object(), filter: x => x > 0);", 1, "Persistence")]
    [InlineData("ExampleFilters", "collection.Find(x => x > 0);", 0, "Persistence")]
    [InlineData("Queries", "collection.Find(ExampleFilters.Active());", 0, "Persistence")]
    [InlineData("Queries", "collection.Find(ExampleFilters.ByIds(System.Linq.Enumerable.Select(new[] { 1 }, x => x)));",
        0, "Persistence")]
    [InlineData("Queries", "new System.Collections.Generic.List<int>().Find(x => x > 0);", 0, "Persistence")]
    public async Task Identifies_driver_filter_arguments_without_flagging_named_filters_or_list_predicates(
        string owner,
        string statement,
        int expected,
        string directory)
    {
        // These signatures model the driver's expression overloads; semantic ownership is essential.
        var source = $$"""
            namespace MongoDB.Driver
            {
                public class Collection
                {
                    public void Find(System.Linq.Expressions.Expression<System.Func<int, bool>> filter) { }
                    public void Delete(object session, System.Linq.Expressions.Expression<System.Func<int, bool>> filter) { }
                }
            }
            namespace Example
            {
                partial class ExampleFilters
                {
                    public static System.Linq.Expressions.Expression<System.Func<int, bool>> Active() => x => x > 0;
                    public static System.Linq.Expressions.Expression<System.Func<int, bool>> ByIds(System.Collections.Generic.IEnumerable<int> ids) => x => x > 0;
                }
                partial class {{owner}}
                {
                    void Query(MongoDB.Driver.Collection collection) { {{statement}} }
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Filters",
            [CSharpSyntaxTree.ParseText(source, path: $"/src/Example.Application/{directory}/Queries.cs")],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostics = await compilation.WithAnalyzers([new MongoInlineFilterAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        Assert.Equal(expected, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal(MongoInlineFilterAnalyzer.DiagnosticId, diagnostic.Id));
    }
}
