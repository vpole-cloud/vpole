using System.Diagnostics;
using System.Xml.Linq;
using Pole.Architecture.FSharp;
using Pole.ArchitectureAnalyzers;
using Xunit;
using Xunit.Abstractions;

namespace Pole.Tests.Architecture;

public sealed class RawBsonProjectTests(ITestOutputHelper output)
{
    private static readonly Lazy<ArchitectureProjectSnapshot> ProjectInventory = new(() =>
        ArchitectureProjectInputs.Load(ProjectFiles(ArchitectureSources.RepositoryRoot)));

    [Fact]
    public void Analyzer_failures_are_build_errors()
    {
        var props = XDocument.Load(Path.Combine(ArchitectureSources.RepositoryRoot, "Directory.Build.props"));
        Assert.Contains(props.Descendants("WarningsAsErrors"), property =>
            property.Value.Split(';').Contains("AD0001", StringComparer.Ordinal));
    }

    [Fact]
    public void Production_FSharp_projects_do_not_use_unapproved_raw_BSON()
    {
        var projects = Projects().Where(project => project.Path.EndsWith(".fsproj", StringComparison.Ordinal) &&
            !project.Exempt).ToArray();
        Assert.NotEmpty(projects);

        output.WriteLine($"architecture.inputs: {ProjectInventory.Value.Elapsed.TotalMilliseconds:F0} ms; {Projects().Count()} projects");
        var violations = projects.SelectMany(project =>
        {
            var stopwatch = Stopwatch.StartNew();
            var result = RawBsonCheck.CheckProject(project.Path, project.CompilerArguments);
            output.WriteLine($"architecture.fsharp.semantic: {stopwatch.Elapsed.TotalMilliseconds:F0} ms; {Path.GetFileName(project.Path)}");
            return result;
        }).ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations.Select(violation =>
            $"{violation.FilePath}:{violation.Line}:{violation.Column} {violation.Rule}: {violation.Message}")));
    }

    [Fact]
    public void Project_roles_are_valid_and_test_support_cannot_escape_into_production()
    {
        var projects = Projects().ToDictionary(project => project.Path, StringComparer.Ordinal);
        Assert.All(projects.Values, project => Assert.Contains(project.Role,
            new[] { ArchitectureProjectRoles.Production, ArchitectureProjectRoles.Migration,
                ArchitectureProjectRoles.TestSupport }));

        foreach (var project in projects.Values.Where(project => !project.IsTest &&
                     project.Role != ArchitectureProjectRoles.TestSupport))
        {
            foreach (var target in project.References)
            {
                if (projects.TryGetValue(target, out var dependency))
                    Assert.True(!dependency.IsTest && dependency.Role != ArchitectureProjectRoles.TestSupport,
                        $"Production project {project.Path} references test-only project {target}.");
            }
        }
    }

    [Theory]
    [InlineData("Debug", "Production", false, 1)]
    [InlineData("Tests", "TestSupport", true, 2)]
    [InlineData("Support", "TestSupport", false, 1)]
    [InlineData("Migration", "Migration", false, 1)]
    [InlineData("ProductionTests", "Production", true, 1)]
    public void Project_metadata_respects_imports_and_conditions(
        string configuration, string role, bool isTest, int referenceCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "pole-project-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Feature.fsproj");
            File.WriteAllText(path, """
                <Project>
                  <PropertyGroup>
                    <PoleArchitectureRole>Production</PoleArchitectureRole>
                    <IsTestProject>false</IsTestProject>
                  </PropertyGroup>
                  <Import Project="metadata.props" />
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "metadata.props"), """
                <Project>
                  <PropertyGroup>
                    <AssemblyName>Feature.$(Configuration)</AssemblyName>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(Configuration)' == 'Tests'">
                    <PoleArchitectureRole>TestSupport</PoleArchitectureRole>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(Configuration)' == 'Support'">
                    <PoleArchitectureRole>TestSupport</PoleArchitectureRole>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(Configuration)' == 'Migration'">
                    <PoleArchitectureRole>Migration</PoleArchitectureRole>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(Configuration)' == 'ProductionTests'">
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Always.csproj" />
                    <ProjectReference Include="TestOnly.csproj" Condition="'$(Configuration)' == 'Tests'" />
                    <ProjectReference Include="UnexpectedDesignTime.csproj" Condition="'$(DesignTimeBuild)' == 'true'" />
                  </ItemGroup>
                </Project>
                """);
            var fixture = XDocument.Load(path);
            fixture.Root!.Add(new XElement("Import", new XAttribute("Project",
                Path.Combine(ArchitectureSources.RepositoryRoot, "tests/Pole.Tests.Architecture/ProjectInputs.targets"))));
            fixture.Save(path);
            var project = Assert.Single(ArchitectureProjectInputs.Load([path], configuration,
                includeCompilerInputs: false).Projects);

            Assert.Equal(role, project.Role);
            Assert.Equal(isTest, project.IsTest);
            Assert.Equal("Feature." + configuration, project.AssemblyName);
            Assert.Equal(configuration, project.Configuration);
            Assert.Equal(configuration != "Debug", project.Exempt);
            Assert.Equal(referenceCount, project.References.Length);
            Assert.Contains(Path.Combine(root, "Always.csproj"), project.References);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Batch_metadata_is_fresh_and_keeps_each_projects_references()
    {
        var root = Path.Combine(Path.GetTempPath(), "pole-project-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new[] { "First.csproj", "Second.fsproj" }.Select(name => Path.Combine(root, name)).ToArray();
            var collector = Path.Combine(ArchitectureSources.RepositoryRoot, "tests/Pole.Tests.Architecture/ProjectInputs.targets");
            foreach (var path in paths)
            {
                new XDocument(new XElement("Project",
                    new XElement("PropertyGroup", new XElement("PoleArchitectureRole", "Production")),
                    new XElement("Import", new XAttribute("Project", "references.props")),
                    new XElement("Import", new XAttribute("Project", collector)))).Save(path);
            }
            var props = Path.Combine(root, "references.props");
            File.WriteAllText(props, "<Project><ItemGroup><ProjectReference Include=\"Before.csproj\" /></ItemGroup></Project>");
            var before = ArchitectureProjectInputs.Load(paths, includeCompilerInputs: false);
            File.WriteAllText(props, "<Project><ItemGroup><ProjectReference Include=\"After.csproj\" /></ItemGroup></Project>");
            var after = ArchitectureProjectInputs.Load(paths, includeCompilerInputs: false);

            Assert.Equal(2, after.Projects.Length);
            Assert.All(before.Projects, project => Assert.Equal(Path.Combine(root, "Before.csproj"), Assert.Single(project.References)));
            Assert.All(after.Projects, project => Assert.Equal(Path.Combine(root, "After.csproj"), Assert.Single(project.References)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_projects_fail_the_batch_instead_of_disappearing()
    {
        var path = Path.Combine(Path.GetTempPath(), "pole-missing-" + Guid.NewGuid().ToString("N"), "Missing.csproj");
        Assert.Throws<InvalidOperationException>(() => ArchitectureProjectInputs.Load([path], includeCompilerInputs: false));
    }

    [Fact]
    public void Project_discovery_does_not_include_sibling_worktrees_or_build_outputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "pole-project-discovery-" + Guid.NewGuid().ToString("N"));
        var own = new[] { "src/Feature/Feature.fsproj", "tests/Fixture/Fixture.csproj" };
        var foreign = new[] { ".tmp/worktrees/old/src/Feature/Feature.fsproj", "src/Feature/obj/Probe.csproj",
            "src/Feature/.tmp/Other.csproj", "src/Feature/node_modules/Other.csproj" };
        try
        {
            foreach (var relative in own.Concat(foreign))
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "<Project />");
            }

            Assert.Equal(own, ProjectFiles(root).Select(path => Path.GetRelativePath(root, path))
                .OrderBy(path => path, StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IEnumerable<string> ProjectFiles(string root) =>
        new[] { "src", "tests" }.Select(directory => Path.Combine(root, directory))
            .Where(Directory.Exists)
            .SelectMany(directory => ArchitectureSources.SourceFiles(directory, "*.*proj"))
            .Where(path => Path.GetExtension(path) is ".csproj" or ".fsproj");

    private static IEnumerable<ArchitectureProject> Projects() => ProjectInventory.Value.Projects;
}
