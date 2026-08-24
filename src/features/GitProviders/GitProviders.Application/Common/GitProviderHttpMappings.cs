using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.GitProviders.Contracts;

namespace Pole.Features.GitProviders.Application.Common;

public static class GitProviderHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<GitHubIntegrationNotConfigured>(MapGitHubIntegrationNotConfigured),
            HttpProblemMapping.For<GitProviderConnectionNotFound>(MapConnectionNotFound),
            HttpProblemMapping.For<GitProviderConnectionHasSourceRepositories>(
                MapConnectionHasSourceRepositories),
            HttpProblemMapping.For<SourceRepositoryNotFound>(MapSourceRepositoryNotFound),
            HttpProblemMapping.For<SourceRepositoryAlreadyLinked>(MapSourceRepositoryAlreadyLinked),
            HttpProblemMapping.For<SourceRepositoryBuildPlanNotFound>(MapBuildPlanNotFound),
            HttpProblemMapping.For<SourceRepositorySelectedBuildPlanUnavailable>(
                MapSelectedBuildPlanUnavailable),
            HttpProblemMapping.For<SourceRepositoryHasBuildConfiguration>(
                MapSourceRepositoryHasBuildConfiguration),
            HttpProblemMapping.For<SourceRepositoryBuildNotFound>(MapSourceRepositoryBuildNotFound),
            HttpProblemMapping.For<RepositorySourceSnapshotTooLarge>(MapSnapshotTooLarge),
            HttpProblemMapping.For<GitProviderUnsupported>(MapProviderUnsupported),
            HttpProblemMapping.For<GitProviderUnavailable>(MapProviderUnavailable)
        ]);

    private static HttpProblem MapGitHubIntegrationNotConfigured(GitHubIntegrationNotConfigured _) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.github.not-configured",
            "GitHub App integration is not configured");

    private static HttpProblem MapConnectionNotFound(GitProviderConnectionNotFound notFound) =>
        Problem(
            HttpStatusCode.NotFound,
            "git-providers.connections.not-found",
            "Git provider connection not found",
            ("connectionId", notFound.ConnectionId));

    private static HttpProblem MapConnectionHasSourceRepositories(
        GitProviderConnectionHasSourceRepositories conflict) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.connections.has-source-repositories",
            "Unlink source repositories before deleting this provider connection",
            ("connectionId", conflict.ConnectionId));

    private static HttpProblem MapSourceRepositoryNotFound(SourceRepositoryNotFound notFound) =>
        Problem(
            HttpStatusCode.NotFound,
            "git-providers.source-repositories.not-found",
            "Source repository not found",
            ("sourceRepositoryId", notFound.SourceRepositoryId));

    private static HttpProblem MapSourceRepositoryBuildNotFound(
        SourceRepositoryBuildNotFound notFound) =>
        Problem(
            HttpStatusCode.NotFound,
            "git-providers.source-repositories.not-found",
            "Source repository not found",
            ("sourceRepositoryId", notFound.SourceRepositoryId));

    private static HttpProblem MapSourceRepositoryAlreadyLinked(SourceRepositoryAlreadyLinked conflict) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.source-repositories.already-linked",
            "This repository branch and build-plan candidate is already linked",
            ("connectionId", conflict.ConnectionId),
            ("repositoryId", conflict.RepositoryId),
            ("branch", conflict.Branch),
            ("candidateKey", conflict.CandidateKey));

    private static HttpProblem MapBuildPlanNotFound(SourceRepositoryBuildPlanNotFound notFound) =>
        Problem(
            HttpStatusCode.UnprocessableEntity,
            "git-providers.source-repositories.build-plan-not-found",
            "The selected build-plan candidate was not found in the latest analysis",
            ("candidateKey", notFound.CandidateKey));

    private static HttpProblem MapSelectedBuildPlanUnavailable(
        SourceRepositorySelectedBuildPlanUnavailable unavailable) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.source-repositories.selected-build-plan-unavailable",
            "The previously selected build-plan candidate no longer exists",
            ("sourceRepositoryId", unavailable.SourceRepositoryId),
            ("candidateKey", unavailable.CandidateKey));

    private static HttpProblem MapSourceRepositoryHasBuildConfiguration(
        SourceRepositoryHasBuildConfiguration conflict) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.source-repositories.has-build-configuration",
            "Delete the source build configuration before unlinking this repository",
            ("sourceRepositoryId", conflict.SourceRepositoryId));

    private static HttpProblem MapSnapshotTooLarge(RepositorySourceSnapshotTooLarge _) =>
        Problem(
            HttpStatusCode.UnprocessableEntity,
            "git-providers.repository-analysis.snapshot-too-large",
            "The repository tree is too large for automatic analysis");

    private static HttpProblem MapProviderUnsupported(GitProviderUnsupported unsupported) =>
        Problem(
            HttpStatusCode.UnprocessableEntity,
            "git-providers.provider.unsupported",
            "The git provider connection is not supported",
            ("provider", unsupported.Provider));

    private static HttpProblem MapProviderUnavailable(GitProviderUnavailable unavailable) =>
        Problem(
            HttpStatusCode.BadGateway,
            "git-providers.provider.unavailable",
            "The git provider could not complete the repository request",
            ("provider", unavailable.Provider));

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) =>
        new(
            (int)status,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: extensions.ToDictionary(
                extension => extension.Name,
                extension => extension.Value));
}
