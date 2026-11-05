using System.Xml.Linq;
using Pole.Architecture.FSharp;
using Pole.ArchitectureAnalyzers;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class RawBsonProjectTests
{
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
            var xml = XDocument.Load(project.Path);
            foreach (var reference in xml.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(include, Path.GetDirectoryName(project.Path)!);
                if (projects.TryGetValue(target, out var dependency))
                    Assert.True(!dependency.IsTest && dependency.Role != ArchitectureProjectRoles.TestSupport,
                        $"Production project {project.Path} references test-only project {target}.");
            }
        }
    }

    [Fact]
    public void Project_discovery_does_not_include_sibling_worktrees_or_build_outputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "pole-project-discovery-" + Guid.NewGuid().ToString("N"));
        var own = new[] { "src/Feature/Feature.fsproj", "tests/Fixture/Fixture.csproj" };
        var foreign = new[] { ".tmp/worktrees/old/src/Feature/Feature.fsproj", "src/Feature/obj/Probe.csproj" };
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

    private static IEnumerable<Project> Projects()
    {
        foreach (var path in ProjectFiles(ArchitectureSources.RepositoryRoot))
        {
            var xml = XDocument.Load(path);
            var explicitTest = xml.Descendants("IsTestProject").LastOrDefault()?.Value;
            var test = bool.TryParse(explicitTest, out var declaredTest) ? declaredTest :
                xml.Descendants("PackageReference")
                    .Any(package => package.Attribute("Include")?.Value == "Microsoft.NET.Test.Sdk");
            var name = Path.GetFileNameWithoutExtension(path);
            var role = xml.Descendants("PoleArchitectureRole").LastOrDefault()?.Value ??
                (name == "Migrations" || name.EndsWith(".Migrations", StringComparison.Ordinal)
                    ? ArchitectureProjectRoles.Migration : ArchitectureProjectRoles.Production);
            yield return new(Path.GetFullPath(path), role, test);
        }
    }

    private sealed record Project(
        string Path,
        string Role,
        bool IsTest)
    {
        public bool Exempt => ArchitectureProjectRoles.ExemptsRawBson(Role, IsTest);
    }
}
