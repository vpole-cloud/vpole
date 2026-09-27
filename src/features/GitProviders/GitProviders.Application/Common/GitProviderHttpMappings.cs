using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Application.ProviderOperations;

namespace Pole.Features.GitProviders.Application.Common;

public static class GitProviderHttpMappings
{
    public static HttpProblem MapAnalyzeError(AnalyzeProviderRepositoryError error) =>
        error.Value switch
        {
            GitProviderConnectionNotFound notFound => MapConnectionNotFound(notFound),
            GitProviderRepositoryAccessError accessError => MapRepositoryAccessError(accessError),
            RepositorySourceSnapshotTooLarge tooLarge => MapSnapshotTooLarge(tooLarge)
        };

    public static HttpProblem MapRepositoryAccessError(GitProviderRepositoryAccessError error) =>
        error.Value switch
        {
            GitProviderUnsupported unsupported => MapProviderUnsupported(unsupported),
            GitProviderUnavailable unavailable => MapProviderUnavailable(unavailable)
        };

    public static HttpProblem MapGitHubIntegrationNotConfigured(GitHubIntegrationNotConfigured _) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.github.not-configured",
            "GitHub App integration is not configured");

    public static HttpProblem MapConnectionNotFound(GitProviderConnectionNotFound notFound) =>
        Problem(
            HttpStatusCode.NotFound,
            "git-providers.connections.not-found",
            "Git provider connection not found",
            ("connectionId", notFound.ConnectionId));

    public static HttpProblem MapConnectionHasSourceRepositories(
        GitProviderConnectionHasSourceRepositories conflict) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.connections.has-source-repositories",
            "Unlink source repositories before deleting this provider connection",
            ("connectionId", conflict.ConnectionId));

    public static HttpProblem MapSourceRepositoryNotFound(SourceRepositoryNotFound notFound) =>
        Problem(
            HttpStatusCode.NotFound,
            "git-providers.source-repositories.not-found",
            "Source repository not found",
            ("sourceRepositoryId", notFound.SourceRepositoryId));

    public static HttpProblem MapSourceRepositoryAlreadyLinked(SourceRepositoryAlreadyLinked conflict) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.source-repositories.already-linked",
            "This repository branch and build-plan candidate is already linked",
            ("connectionId", conflict.ConnectionId),
            ("repositoryId", conflict.RepositoryId),
            ("branch", conflict.Branch),
            ("candidateKey", conflict.CandidateKey));

    public static HttpProblem MapBuildPlanNotFound(SourceRepositoryBuildPlanNotFound notFound) =>
        Problem(
            HttpStatusCode.UnprocessableEntity,
            "git-providers.source-repositories.build-plan-not-found",
            "The selected build-plan candidate was not found in the latest analysis",
            ("candidateKey", notFound.CandidateKey));

    public static HttpProblem MapSelectedBuildPlanUnavailable(
        SourceRepositorySelectedBuildPlanUnavailable unavailable) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.source-repositories.selected-build-plan-unavailable",
            "The previously selected build-plan candidate no longer exists",
            ("sourceRepositoryId", unavailable.SourceRepositoryId),
            ("candidateKey", unavailable.CandidateKey));

    public static HttpProblem MapSourceRepositoryHasBuildConfiguration(
        SourceRepositoryHasBuildConfiguration conflict) =>
        Problem(
            HttpStatusCode.Conflict,
            "git-providers.source-repositories.has-build-configuration",
            "Delete the source build configuration before unlinking this repository",
            ("sourceRepositoryId", conflict.SourceRepositoryId));

    public static HttpProblem MapSnapshotTooLarge(RepositorySourceSnapshotTooLarge _) =>
        Problem(
            HttpStatusCode.UnprocessableEntity,
            "git-providers.repository-analysis.snapshot-too-large",
            "The repository tree is too large for automatic analysis");

    public static HttpProblem MapProviderUnsupported(GitProviderUnsupported unsupported) =>
        Problem(
            HttpStatusCode.UnprocessableEntity,
            "git-providers.provider.unsupported",
            "The git provider connection is not supported",
            ("provider", unsupported.Provider));

    public static HttpProblem MapProviderUnavailable(GitProviderUnavailable unavailable) =>
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
