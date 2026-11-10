using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class GitOpsCompilerBoundaryTests
{
    private const string CompilerDirectory = "src/features/GitOps/GitOps.Compiler/";

    [Fact]
    public void Compiler_ReferencesOnlyDeclarationContractsPrimitivesAndYaml()
    {
        var project = LoadProject(CompilerDirectory + "GitOps.Compiler.csproj");
        var references = Values(project, "ProjectReference");
        Assert.Equal(
            ["../GitOps.Contracts/GitOps.Contracts.csproj", "../GitOps.Primitives/GitOps.Primitives.csproj"],
            references);
        Assert.Equal(["YamlDotNet"], Values(project, "PackageReference"));
    }

    [Fact]
    public void CompilerTests_RunWithoutTheApplicationAssembly()
    {
        var project = LoadProject("src/features/GitOps/GitOps.Compiler.Tests/GitOps.Compiler.Tests.csproj");
        Assert.Equal(["../GitOps.Compiler/GitOps.Compiler.csproj"], Values(project, "ProjectReference"));
    }

    [Fact]
    public void Compiler_DoesNotUseRuntimeDatabaseOrTransportApis()
    {
        string[] forbiddenNamespaces =
        [
            "Pole.Features.GitOps.Application",
            "Pole.Features.GitOps.Domain",
            "Pole.Features.GitOps.Events",
            "MongoDB",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions.DependencyInjection",
            "System.Net",
        ];

        var violations = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.StartsWith(CompilerDirectory, StringComparison.Ordinal))
            .SelectMany(source =>
                source.Root
                    .DescendantNodes()
                    .OfType<NameSyntax>()
                    .Where(name => forbiddenNamespaces.Any(prefix =>
                        name.ToString() == prefix ||
                        name.ToString().StartsWith(prefix + ".", StringComparison.Ordinal)))
                    .Select(name => ArchitectureSources.Describe(
                        source,
                        name,
                        "Manifest compilation must not depend on runtime, database or transport APIs")))
            .ToArray();

        Assert.Empty(violations);
    }

    private static XDocument LoadProject(string path) =>
        XDocument.Load(Path.Combine(ArchitectureSources.RepositoryRoot, path));

    private static string[] Values(XDocument project, string element) => project.Descendants(element)
        .Select(reference => reference.Attribute("Include")!.Value)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
