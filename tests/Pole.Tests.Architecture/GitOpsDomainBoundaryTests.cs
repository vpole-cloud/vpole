using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class GitOpsDomainBoundaryTests
{
    [Fact]
    public void Domain_allows_Mongo_attributes_but_has_no_transport_or_feature_dependencies()
    {
        var project = XDocument.Load(Path.Combine(ArchitectureSources.RepositoryRoot,
            "src/features/GitOps/GitOps.Domain/GitOps.Domain.csproj"));
        Assert.All(project.Descendants("ProjectReference"), reference =>
            Assert.Contains(reference.Attribute("Include")?.Value, new[]
            {
                "../GitOps.Primitives/GitOps.Primitives.csproj",
                "../../../shared/Multitenancy/Multitenancy.csproj"
            }));
        Assert.All(project.Descendants("PackageReference"), reference =>
            Assert.Equal("MongoDB.Driver", reference.Attribute("Include")?.Value));
        var sources = ArchitectureSources.ProductionCSharp().Where(source => source.RelativePath.StartsWith(
            "src/features/GitOps/GitOps.Domain/", StringComparison.Ordinal));
        var violations = sources.SelectMany(source => source.Root.DescendantNodes()
                .OfType<QualifiedNameSyntax>().Where(name => name.Parent is not QualifiedNameSyntax)
                .Select(name => (Source: source, Name: name)))
            .Where(item => Forbidden(item.Name.ToString()))
            .Select(item => ArchitectureSources.Describe(item.Source, item.Name,
                "GitOps domain must not import application persistence, transport or feature contracts"));
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("MongoDB.Driver", false)]
    [InlineData("MongoDB.Bson.Serialization.Attributes", false)]
    [InlineData("System.Text.Json.Serialization", true)]
    [InlineData("YamlDotNet.Core", true)]
    [InlineData("Microsoft.AspNetCore.Http", true)]
    [InlineData("System.Net.Http", true)]
    [InlineData("Pole.Features.GitOps.Contracts", true)]
    [InlineData("Pole.Features.Secrets.Domain", true)]
    [InlineData("Pole.Database", true)]
    [InlineData("System.Collections.Immutable", false)]
    [InlineData("Pole.Features.GitOps.Domain", false)]
    [InlineData("Pole.Features.GitOps.Primitives", false)]
    [InlineData("Pole.Features.Core.Multitenancy", false)]
    [InlineData("Pole.Features.Core.Multitenancy.ITenantOwned", false)]
    [InlineData("Pole.Features.Core.MultitenancyExtra", true)]
    [InlineData("Pole.Features.GitOps.PrimitivesExtra", true)]
    [InlineData("Pole.Features.GitOps.Domainish", true)]
    public void Boundary_rule_distinguishes_technical_boundaries_from_pure_domain_dependencies(string name,
        bool forbidden)
    {
        var syntax = CSharpSyntaxTree.ParseText("using " + name + ";").GetRoot();
        var directive = Assert.Single(syntax.DescendantNodes().OfType<UsingDirectiveSyntax>());
        Assert.Equal(forbidden, Forbidden(directive.Name!.ToString()));
    }

    [Fact]
    public void Boundary_rule_rejects_an_aliased_globally_qualified_http_client()
    {
        var syntax = CSharpSyntaxTree.ParseText("using Client = global::System.Net.Http.HttpClient;").GetRoot();
        var directive = Assert.Single(syntax.DescendantNodes().OfType<UsingDirectiveSyntax>());
        Assert.True(Forbidden(directive.Name!.ToString()));
    }

    private static bool Forbidden(string name)
    {
        name = name.Replace("global::", "", StringComparison.Ordinal);
        return name.StartsWith("System.Text.Json", StringComparison.Ordinal) ||
            name.StartsWith("System.Net.Http", StringComparison.Ordinal) ||
            name.StartsWith("YamlDotNet", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            name.StartsWith("Pole.Database", StringComparison.Ordinal) ||
            name.StartsWith("Pole.Features.", StringComparison.Ordinal) &&
            !(name == "Pole.Features.GitOps.Domain" ||
                name.StartsWith("Pole.Features.GitOps.Domain.", StringComparison.Ordinal) ||
                name == "Pole.Features.GitOps.Primitives" ||
                name.StartsWith("Pole.Features.GitOps.Primitives.", StringComparison.Ordinal) ||
                name == "Pole.Features.Core.Multitenancy" ||
                name.StartsWith("Pole.Features.Core.Multitenancy.", StringComparison.Ordinal));
    }
}
