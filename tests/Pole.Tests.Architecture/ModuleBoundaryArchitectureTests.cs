using System.Xml.Linq;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class ModuleBoundaryArchitectureTests
{
    private static readonly IReadOnlyList<DocumentedProjectException>
        TemporaryProjectReferenceAllowlist = [];

    private static readonly IReadOnlyList<DocumentedProjectException>
        TemporarySharedProjectReferenceAllowlist =
        [
            DocumentedProjectException.Create(
                "StorageProfiles.Contracts",
                "Models",
                "The legacy ResourceType model is pending replacement.",
                "#53"),
            DocumentedProjectException.Create(
                "ResourceGroups.Events",
                "Models",
                "ResourceGroup.Resources still uses the legacy shared resource model.",
                "#53")
        ];

    private static readonly IReadOnlyList<DocumentedSourceException>
        TemporaryIntegrationEventPlacementAllowlist = [];

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

    private sealed record ProjectReferenceEdge(ProjectNode Source, ProjectNode Target);

    private sealed record SharedProjectReferenceEdge(
        ProjectNode Source,
        string TargetIdentity);

    private sealed record ProjectEdge(string Source, string Target);

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
