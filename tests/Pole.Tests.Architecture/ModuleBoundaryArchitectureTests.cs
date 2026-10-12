using System.Xml.Linq;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class ModuleBoundaryArchitectureTests
{
    private static readonly IReadOnlyList<DocumentedProjectException>
        TemporaryProjectReferenceAllowlist = [];

    private static readonly IReadOnlyList<DocumentedProjectException>
        TemporarySharedProjectReferenceAllowlist = [];

    private static readonly IReadOnlyList<DocumentedSourceException>
        TemporaryIntegrationEventPlacementAllowlist = [];

    // Distribution is independent: built-in workloads are still providers to generic capabilities.
    private static readonly IReadOnlyDictionary<string, FeatureRole> FeatureRoles =
        new Dictionary<string, FeatureRole>(StringComparer.Ordinal)
        {
            ["ComputeProfiles"] = FeatureRole.Platform,
            ["ContainerRegistries"] = FeatureRole.Platform,
            ["DeploymentTargets"] = FeatureRole.Platform,
            ["Domains"] = FeatureRole.Platform,
            ["GitProviders"] = FeatureRole.BuiltIn,
            ["Outbox"] = FeatureRole.Platform,
            ["PrivateAccess"] = FeatureRole.Platform,
            ["Quotas"] = FeatureRole.Platform,
            ["Realtime"] = FeatureRole.Platform,
            ["ResourceConnections"] = FeatureRole.Platform,
            ["ResourceDeployments"] = FeatureRole.Platform,
            ["ResourceGroups"] = FeatureRole.Platform,
            ["Search"] = FeatureRole.Platform,
            ["Secrets"] = FeatureRole.Platform,
            ["StorageProfiles"] = FeatureRole.Platform,
            ["Tenants"] = FeatureRole.Platform,
            ["Users"] = FeatureRole.Platform,
            ["Functions"] = FeatureRole.BuiltIn,
            ["SourceBuilds"] = FeatureRole.BuiltIn,
            ["WebServices"] = FeatureRole.BuiltIn,
            ["Postgres"] = FeatureRole.ServiceProvider,
            ["RedisServices"] = FeatureRole.ServiceProvider
        };

    [Fact]
    public void EveryProductionFeature_HasAnExplicitArchitecturalRole()
    {
        var projects = LoadProductionFeatureProjects(Path.Combine(FindRepositoryRoot(), "src/features"));
        Assert.DoesNotContain(projects.Values.Select(project => project.Feature)
            .Distinct(StringComparer.Ordinal), feature => !FeatureRoles.ContainsKey(feature));
    }

    [Fact]
    public void PlatformCapabilities_DoNotReferenceConcreteProviders()
    {
        var projects = LoadProductionFeatureProjects(Path.Combine(FindRepositoryRoot(), "src/features"));
        var violations = projects.Values
            .SelectMany(project => LoadFeatureReferences(project, projects))
            .Where(edge => !IsAllowedFeatureDirection(edge.Source.Feature, edge.Target.Feature))
            .Select(edge => $"{edge.Source.Identity} -> {edge.Target.Identity}")
            .Order(StringComparer.Ordinal);
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("ResourceDeployments", "Postgres", false)]
    [InlineData("ResourceConnections", "RedisServices", false)]
    [InlineData("Search", "WebServices", false)]
    [InlineData("ResourceDeployments", "Functions", false)]
    [InlineData("SourceBuilds", "RedisServices", false)]
    [InlineData("Postgres", "ResourceDeployments", true)]
    [InlineData("Functions", "ResourceConnections", true)]
    [InlineData("SourceBuilds", "WebServices", true)]
    [InlineData("RedisServices", "RedisServices", true)]
    public void ArchitecturalRole_ConstrainsEveryProjectRole(
        string sourceFeature,
        string targetFeature,
        bool expected)
    {
        Assert.Equal(expected, IsAllowedFeatureDirection(sourceFeature, targetFeature));
    }

    private static bool IsAllowedFeatureDirection(string sourceFeature, string targetFeature)
    {
        if (sourceFeature == targetFeature)
            return true;
        var source = FeatureRoles[sourceFeature];
        var target = FeatureRoles[targetFeature];
        return source switch
        {
            FeatureRole.Platform => target is FeatureRole.Platform,
            FeatureRole.BuiltIn => target is not FeatureRole.ServiceProvider,
            FeatureRole.ServiceProvider => true,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceFeature))
        };
    }

    private enum FeatureRole
    {
        Platform,
        BuiltIn,
        ServiceProvider
    }

    [Theory]
    [InlineData("Application", true)]
    [InlineData("Domain", false)]
    [InlineData("Contracts", false)]
    [InlineData("Events", false)]
    [InlineData("Runtime", false)]
    [InlineData("Primitives", false)]
    [InlineData("ContractHttpMapping", false)]
    public void ForeignHttpMappingCompanions_AreAvailableOnlyToApplication(
        string sourceRole,
        bool expected)
    {
        var source = new ProjectNode("Consumer", "Consumer.Project",
            Enum.Parse<ProjectRole>(sourceRole), string.Empty);
        var target = new ProjectNode("Provider", "Provider.Contracts.HttpMapping",
            ProjectRole.ContractHttpMapping, string.Empty);

        Assert.Equal(expected, IsAllowed(source, target));
    }

    [Fact]
    public void ReusableHostingAndTesting_DoNotDependOnConcreteServicesOrTheServer()
    {
        var root = FindRepositoryRoot();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit(Path.Combine(root, "src/api/Pole.Platform.Hosting/Pole.Platform.Hosting.csproj"));
        Visit(Path.Combine(root, "src/api/Pole.Testing/Pole.Testing.csproj"));
        var centralTests = File.ReadAllText(Path.Combine(root, "tests/Pole.Tests.Integration/Pole.Tests.Integration.csproj"));
        Assert.DoesNotContain("RedisServices", centralTests);
        Assert.DoesNotContain("Postgres", centralTests);
        foreach (var feature in new[] { "RedisServices", "Postgres" })
            foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "src/features", feature), "*.*proj", SearchOption.AllDirectories))
                Assert.DoesNotContain("Pole.Tests.Integration.csproj", File.ReadAllText(project));

        void Visit(string project)
        {
            project = Path.GetFullPath(project);
            if (!visited.Add(project)) return;
            Assert.DoesNotContain("Pole.Server.csproj", project);
            foreach (var feature in new[] { "RedisServices", "WebServices", "Postgres", "Functions" })
                Assert.DoesNotContain($"{Path.DirectorySeparatorChar}{feature}{Path.DirectorySeparatorChar}", project);
            foreach (var reference in XDocument.Load(project).Descendants("ProjectReference")
                         .Where(reference => reference.Attribute("ReferenceOutputAssembly")?.Value != "false"))
                Visit(Path.Combine(Path.GetDirectoryName(project)!, reference.Attribute("Include")!.Value
                    .Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    [Fact]
    public void SearchApplication_DoesNotReferenceConcreteResourceFeatures()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "features",
            "Search",
            "Search.Application",
            "Search.Application.fsproj");
        var references = XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFileNameWithoutExtension(
                value!.Replace('\\', Path.DirectorySeparatorChar)))
            .ToArray();

        Assert.DoesNotContain(references, reference =>
            reference.EndsWith(".Events", StringComparison.Ordinal) && reference != "Search.Events" ||
            reference.StartsWith("ResourceDeployments.", StringComparison.Ordinal));

        var sourceText = Directory
            .EnumerateFiles(Path.GetDirectoryName(projectPath)!, "*.fs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText);
        Assert.DoesNotContain(sourceText, source =>
            source.Contains("Pole.Features.ResourceGroups", StringComparison.Ordinal) ||
            source.Contains("Pole.Features.WebServices", StringComparison.Ordinal) ||
            source.Contains("Pole.Features.RedisServices", StringComparison.Ordinal) ||
            source.Contains("Pole.Features.Postgres", StringComparison.Ordinal) ||
            source.Contains("Pole.Features.ResourceDeployments", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchProjectionProviders_ReferenceOnlySearchContracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        string[] applicationProjects =
        [
            "ResourceGroups/ResourceGroups.Application/ResourceGroups.Application.csproj",
            "WebServices/WebServices.Application/WebServices.Application.csproj",
            "RedisServices/RedisServices.Application/RedisServices.Application.csproj",
            "Postgres/Postgres.Application/Postgres.Application.fsproj",
            "ResourceDeployments/ResourceDeployments.Application/ResourceDeployments.Application.csproj"
        ];

        foreach (var relativePath in applicationProjects)
        {
            var projectPath = Path.Combine(
                repositoryRoot,
                "src",
                "features",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var searchReferences = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFileNameWithoutExtension(
                    value!.Replace('\\', Path.DirectorySeparatorChar)))
                .Where(identity => identity.StartsWith("Search.", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(["Search.Contracts"], searchReferences);
        }
    }

    [Fact]
    public void ProductionFeatureProjectReferences_FollowTheModuleDependencyPolicy()
    {
        var repositoryRoot = FindRepositoryRoot();
        var featureRoot = Path.Combine(repositoryRoot, "src", "features");
        var projects = LoadProductionFeatureProjects(featureRoot);
        var violations = projects.Values
            .SelectMany(project => LoadFeatureReferences(project, projects))
            .Where(edge => !IsAllowed(edge.Source, edge.Target))
            .Select(edge => new ProjectEdge(edge.Source.Identity, edge.Target.Identity))
            .ToHashSet();

        var allowlistedEdges = TemporaryProjectReferenceAllowlist
            .Select(exception => exception.Edge)
            .ToHashSet();
        var unexpected = violations
            .Except(allowlistedEdges)
            .OrderBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal)
            .ToArray();
        var staleAllowlistEntries = allowlistedEdges
            .Except(violations)
            .OrderBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unexpected);
        Assert.Empty(staleAllowlistEntries);
    }

    [Fact]
    public void ContractsAndEvents_UseOnlyApprovedSharedProjects()
    {
        var repositoryRoot = FindRepositoryRoot();
        var featureRoot = Path.Combine(repositoryRoot, "src", "features");
        var sharedRoot = Path.Combine(repositoryRoot, "src", "shared");
        var projects = LoadProductionFeatureProjects(featureRoot);
        var violations = projects.Values
            .Where(project => project.Role is
                ProjectRole.Contracts or
                ProjectRole.ContractHttpMapping or
                ProjectRole.Events)
            .SelectMany(project => LoadSharedReferences(project, sharedRoot))
            .Where(edge => !IsAllowedSharedReference(edge.Source, edge.TargetIdentity))
            .Select(edge => new ProjectEdge(edge.Source.Identity, edge.TargetIdentity))
            .ToHashSet();
        var allowlistedEdges = TemporarySharedProjectReferenceAllowlist
            .Select(exception => exception.Edge)
            .ToHashSet();

        Assert.Empty(violations.Except(allowlistedEdges));
        Assert.Empty(allowlistedEdges.Except(violations));
    }

    [Fact]
    public void NamedIntegrationEvents_LiveOnlyInEventsProjects()
    {
        var repositoryRoot = FindRepositoryRoot();
        var featureRoot = Path.Combine(repositoryRoot, "src", "features");
        var projects = LoadProductionFeatureProjects(featureRoot);
        var violations = projects.Values
            .Where(project => project.Role is not ProjectRole.Events)
            .SelectMany(project => Directory.EnumerateFiles(
                Path.GetDirectoryName(project.FullPath)!,
                "*",
                SearchOption.AllDirectories))
            .Where(IsProductionSourceFile)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("[EntityName(", StringComparison.Ordinal) ||
                       source.Contains("[<EntityName", StringComparison.Ordinal);
            })
            .Select(path => Path
                .GetRelativePath(repositoryRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);

        var allowlistedPaths = TemporaryIntegrationEventPlacementAllowlist
            .Select(exception => exception.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var unexpected = violations
            .Except(allowlistedPaths)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var staleAllowlistEntries = allowlistedPaths
            .Except(violations)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unexpected);
        Assert.Empty(staleAllowlistEntries);
    }

    private static IReadOnlyDictionary<string, ProjectNode> LoadProductionFeatureProjects(
        string featureRoot) =>
        Directory
            .EnumerateFiles(featureRoot, "*.*proj", SearchOption.AllDirectories)
            .Select(path => ProjectNode.Create(featureRoot, path))
            .Where(project => project.Role is not ProjectRole.Other)
            .ToDictionary(project => project.FullPath, StringComparer.Ordinal);

    private static IEnumerable<ProjectReferenceEdge> LoadFeatureReferences(
        ProjectNode source,
        IReadOnlyDictionary<string, ProjectNode> projects)
    {
        var document = XDocument.Load(source.FullPath);
        foreach (var reference in document
                     .Descendants("ProjectReference")
                     .Select(element => element.Attribute("Include")?.Value)
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var normalizedReference = reference!
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(source.FullPath)!,
                normalizedReference));
            if (projects.TryGetValue(targetPath, out var target))
                yield return new ProjectReferenceEdge(source, target);
        }
    }

    private static IEnumerable<SharedProjectReferenceEdge> LoadSharedReferences(
        ProjectNode source,
        string sharedRoot)
    {
        var normalizedSharedRoot = Path.GetFullPath(sharedRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var document = XDocument.Load(source.FullPath);
        foreach (var reference in document
                     .Descendants("ProjectReference")
                     .Select(element => element.Attribute("Include")?.Value)
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var normalizedReference = reference!
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(source.FullPath)!,
                normalizedReference));
            if (!targetPath.StartsWith(normalizedSharedRoot, StringComparison.Ordinal))
                continue;

            yield return new SharedProjectReferenceEdge(
                source,
                Path.GetFileNameWithoutExtension(targetPath));
        }
    }

    private static bool IsAllowedSharedReference(
        ProjectNode source,
        string targetIdentity)
    {
        if (source is { Role: ProjectRole.Contracts, Identity: "Outbox.Contracts" } &&
            targetIdentity == "Events")
        {
            return true;
        }

        return source.Role switch
        {
            ProjectRole.Contracts => targetIdentity is "ResultType" or "Workloads",
            ProjectRole.ContractHttpMapping => targetIdentity is "ASP",
            ProjectRole.Events => targetIdentity is "Events" or "Workloads",
            _ => true
        };
    }

    private static bool IsAllowed(ProjectNode source, ProjectNode target) =>
        source.Role switch
        {
            ProjectRole.Primitives => false,
            ProjectRole.Domain or ProjectRole.Contracts or ProjectRole.Events
                when source.Feature == target.Feature =>
                target.Role is ProjectRole.Primitives,
            ProjectRole.Domain or ProjectRole.Contracts or ProjectRole.Events => false,
            ProjectRole.ContractHttpMapping when source.Feature == target.Feature =>
                target.Role is ProjectRole.Contracts,
            ProjectRole.ContractHttpMapping => false,
            ProjectRole.Application when source.Feature == target.Feature =>
                target.Role is ProjectRole.Primitives or
                    ProjectRole.Domain or
                    ProjectRole.Contracts or
                    ProjectRole.ContractHttpMapping or
                    ProjectRole.Events,
            ProjectRole.Application =>
                target.Role is ProjectRole.Contracts or
                    ProjectRole.ContractHttpMapping or
                    ProjectRole.Events ||
                target.Role is ProjectRole.Primitives &&
                    ReferencesProviderContract(source, target.Feature),
            ProjectRole.Runtime when source.Feature == target.Feature =>
                target.Role is ProjectRole.Primitives or
                    ProjectRole.Domain or
                    ProjectRole.Contracts or
                    ProjectRole.Events or
                    ProjectRole.Application,
            ProjectRole.Runtime =>
                target.Role is ProjectRole.Contracts or
                    ProjectRole.Events ||
                target.Role is ProjectRole.Primitives &&
                    ReferencesProviderContract(source, target.Feature),
            _ => true
        };

    private static bool ReferencesProviderContract(
        ProjectNode source,
        string providerFeature)
    {
        var expectedIdentity = $"{providerFeature}.Contracts";
        return XDocument.Load(source.FullPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar))
            .Select(Path.GetFileNameWithoutExtension)
            .Contains(expectedIdentity, StringComparer.Ordinal);
    }

    private static bool IsProductionSourceFile(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.Ordinal) &&
            !path.EndsWith(".fs", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        return !normalized.Contains("/bin/", StringComparison.Ordinal) &&
               !normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pole.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Pole repository root.");
    }

    private sealed record ProjectNode(
        string Feature,
        string Identity,
        ProjectRole Role,
        string FullPath)
    {
        public static ProjectNode Create(string featureRoot, string fullPath)
        {
            var relativePath = Path.GetRelativePath(featureRoot, fullPath);
            var feature = relativePath.Split(Path.DirectorySeparatorChar)[0];
            var identity = Path.GetFileNameWithoutExtension(fullPath);
            return new ProjectNode(feature, identity, GetRole(identity), fullPath);
        }

        private static ProjectRole GetRole(string identity)
        {
            if (identity.EndsWith(".Contracts.HttpMapping", StringComparison.Ordinal))
                return ProjectRole.ContractHttpMapping;
            if (identity.EndsWith(".Domain", StringComparison.Ordinal))
                return ProjectRole.Domain;
            if (identity.EndsWith(".Contracts", StringComparison.Ordinal))
                return ProjectRole.Contracts;
            if (identity.EndsWith(".Events", StringComparison.Ordinal))
                return ProjectRole.Events;
            if (identity.EndsWith(".Application", StringComparison.Ordinal))
                return ProjectRole.Application;
            if (identity.EndsWith(".Runtime", StringComparison.Ordinal))
                return ProjectRole.Runtime;
            if (identity.EndsWith(".Primitives", StringComparison.Ordinal))
                return ProjectRole.Primitives;
            return ProjectRole.Other;
        }
    }

    private sealed record ProjectReferenceEdge(
        ProjectNode Source,
        ProjectNode Target);

    private sealed record SharedProjectReferenceEdge(
        ProjectNode Source,
        string TargetIdentity);

    private sealed record ProjectEdge(
        string Source,
        string Target);

    private sealed record DocumentedProjectException(
        ProjectEdge Edge,
        string Reason,
        string TrackingIssue)
    {
        public static DocumentedProjectException Create(
            string source,
            string target,
            string reason,
            string trackingIssue)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(target);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            ArgumentException.ThrowIfNullOrWhiteSpace(trackingIssue);
            return new(new ProjectEdge(source, target), reason, trackingIssue);
        }
    }

    private sealed record DocumentedSourceException(
        string RelativePath,
        string Reason,
        string TrackingIssue)
    {
        public static DocumentedSourceException Create(
            string relativePath,
            string reason,
            string trackingIssue)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            ArgumentException.ThrowIfNullOrWhiteSpace(trackingIssue);
            return new(relativePath, reason, trackingIssue);
        }
    }

    private enum ProjectRole
    {
        Other,
        Domain,
        Contracts,
        ContractHttpMapping,
        Events,
        Application,
        Runtime,
        Primitives
    }
}
