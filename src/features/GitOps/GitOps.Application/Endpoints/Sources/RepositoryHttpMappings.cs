using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Features.GitOps.Application.Sources.Mongo;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

public sealed record RepositoryProviderUnsupported;

internal static class RepositoryHttpMappings
{
    public static IResult Map(GitSourceNotFound _) =>
        Problem(404, "gitops.source-not-found", "Source or run not found");

    public static IResult Map(GitSourceRevisionConflict _) =>
        Problem(409, "gitops.revision-conflict", "Source revision changed");

    public static IResult Map(GitSourceIdempotencyConflict _) =>
        Problem(409, "gitops.idempotency-conflict", "The key was already used for different input");

    public static IResult Map(GitSourceInactive _) =>
        Problem(409, "gitops.source-inactive", "Source activity does not allow this command");

    public static IResult Map(GitSourceCreationDeleted _) =>
        Problem(409, "gitops.source-deleted", "This creation key belongs to a deleted source");

    public static IResult Map(GitSourceDetachRequired _) =>
        Problem(409, "gitops.detach-required", "Detach source resources before deleting the source");

    public static IResult Map(GitSourceBusy _) => Problem(409, "gitops.source-busy", "Source has pending work");

    public static IResult Map(GitRetryNotAvailable _) =>
        Problem(409, "gitops.retry-not-available", "The run cannot be retried; request a sync");

    public static IResult Map(AdoptionCandidateSearchUnsupported _) =>
        Problem(400, "gitops.candidate-query-unsupported", "The provider does not support this candidate search");

    public static IResult Map(RepositoryProviderUnsupported _) =>
        Problem(400, "gitops.provider-unsupported", "Repository provider is not registered");

    public static IResult Map(RepositoryAccessDenied _) =>
        Problem(403, "gitops.repository-access-denied", "Repository access denied");

    public static IResult Map(RepositoryNotFound _) =>
        Problem(404, "gitops.repository-not-found", "Repository not found");

    public static IResult Map(RepositoryTemporarilyUnavailable _) =>
        Problem(503, "gitops.repository-unavailable", "Repository provider is temporarily unavailable");

    private static IResult Problem(int status, string code, string title) =>
        new HttpProblem(status, PoleProblemTypeId.FromCode(code), title, code).ToHttpResult();
}
