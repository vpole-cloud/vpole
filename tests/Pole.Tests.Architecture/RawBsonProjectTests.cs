using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Pole.Architecture.FSharp;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class RawBsonProjectTests
{
    private static readonly Lazy<Project[]> ProjectInventory = new(() =>
        ProjectFiles(ArchitectureSources.RepositoryRoot).AsParallel().WithDegreeOfParallelism(2)
            .Select(path => EvaluateProject(path)).ToArray());

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

        var violations = projects.SelectMany(project => RawBsonCheck.CheckProject(project.Path,
            RawBsonCheck.GetCompilerArguments(project.Path))).ToArray();

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
                  <PropertyGroup Condition="'$(Configuration)' == 'Tests'">
                    <PoleArchitectureRole>TestSupport</PoleArchitectureRole>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Always.csproj" />
                    <ProjectReference Include="TestOnly.csproj" Condition="'$(Configuration)' == 'Tests'" />
                  </ItemGroup>
                </Project>
                """);

            var project = EvaluateProject(path, configuration);

            Assert.Equal(role, project.Role);
            Assert.Equal(isTest, project.IsTest);
            Assert.Equal(referenceCount, project.References.Length);
            Assert.Contains(Path.Combine(root, "Always.csproj"), project.References);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static IEnumerable<Project> Projects() => ProjectInventory.Value;

    private static Project EvaluateProject(string path, string? configuration = null)
    {
        path = Path.GetFullPath(path);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["MSBUILDUSESERVER"] = "0";
        foreach (var argument in new[] { "msbuild", path, "-getProperty:PoleArchitectureRole,IsTestProject",
                     "-getItem:ProjectReference", "-nologo", "-maxcpucount:1", "-nodeReuse:false" })
            start.ArgumentList.Add(argument);
        if (configuration is not null)
            start.ArgumentList.Add("-property:Configuration=" + configuration);

        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start MSBuild.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30_000))
        {
            child.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"Timed out reading architecture metadata for {path}.");
        }

        var stdout = output.GetAwaiter().GetResult();
        var stderr = error.GetAwaiter().GetResult();
        Assert.True(child.ExitCode == 0, $"Could not evaluate {path}:{Environment.NewLine}{stdout}{stderr}");
        using var json = JsonDocument.Parse(stdout);
        var properties = json.RootElement.GetProperty("Properties");
        var role = properties.GetProperty("PoleArchitectureRole").GetString()!;
        var isTest = bool.TryParse(properties.GetProperty("IsTestProject").GetString(), out var value) && value;
        var references = json.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
            .Select(reference => reference.GetProperty("FullPath").GetString()!).ToArray();
        return new(path, role, isTest, references);
    }

    private sealed record Project(
        string Path,
        string Role,
        bool IsTest,
        string[] References)
    {
        public bool Exempt => ArchitectureProjectRoles.ExemptsMongoRules(Role, IsTest);
    }
}
