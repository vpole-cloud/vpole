using System.Text.RegularExpressions;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class FSharpDomainConstructorDefaultsTests
{
    [Fact]
    public void Domain_constructors_do_not_declare_optional_arguments()
    {
        var violations = ArchitectureSources.ProductionFSharp()
            .Where(source => source.RelativePath.Contains(".Domain/", StringComparison.Ordinal))
            .Where(source => HasOptionalConstructor(source.Text))
            .Select(source => source.RelativePath);

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("type Model(?revision: int) = class end", true)]
    [InlineData("type Model private (?revision: int) = class end", true)]
    [InlineData("type Model(revision: int) = new (?enabled: bool) = Model(1)", true)]
    [InlineData("type Model(revision: int) = class end", false)]
    [InlineData("type Factory() = static member Create(?revision: int) = 1", false)]
    public void Constructor_arguments_are_distinguished_from_factory_methods(string source, bool rejected)
    {
        Assert.Equal(rejected, HasOptionalConstructor(source));
    }

    private static bool HasOptionalConstructor(string source) => Regex.IsMatch(source,
        @"(?:\btype\s+\w+(?:<[^>]+>)?\s*(?:(?:private|internal|public)\s*)?|\bnew\s*)\([^)]*\?\w+\s*:",
        RegexOptions.CultureInvariant);
}
