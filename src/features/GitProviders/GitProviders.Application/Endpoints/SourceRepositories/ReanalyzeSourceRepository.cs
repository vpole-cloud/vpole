using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record ReanalyzeSourceRepositoryRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<ReanalyzeSourceRepositoryOutcome>, ITenantWriteOperation;

public union ReanalyzeSourceRepositoryError(
    SourceRepositoryNotFound,
    AnalyzeProviderRepositoryError,
    SourceRepositorySelectedBuildPlanUnavailable);

public union ReanalyzeSourceRepositoryOutcome(
    SourceRepositoryDto,
    ReanalyzeSourceRepositoryError);

public sealed class ReanalyzeSourceRepositoryHandler(
    LoadSourceRepository loadSourceRepository,
    AnalyzeProviderRepository analyzeProviderRepository,
    PersistReanalyzedSourceRepository persistReanalyzedSourceRepository,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<ReanalyzeSourceRepositoryRequest, ReanalyzeSourceRepositoryOutcome>
{
    public async Task<ReanalyzeSourceRepositoryOutcome> HandleAsync(
        ReanalyzeSourceRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await loadSourceRepository(request.TenantId, request.Id, cancellationToken);
        if (existing is null)
            return new ReanalyzeSourceRepositoryError(new SourceRepositoryNotFound(request.Id));

        var analyzed = await analyzeProviderRepository(
            request.TenantId,
            existing.ConnectionId,
            existing.ExternalRepositoryId,
            existing.Branch,
            cancellationToken);
        return analyzed switch
        {
            Err<AnalyzeProviderRepositoryError> error =>
                new ReanalyzeSourceRepositoryError(error.Value),
            Ok<AnalyzedProviderRepository> success => await PersistAsync(
                existing,
                success.Value,
                persistReanalyzedSourceRepository,
                timeProvider,
                cancellationToken)
        };
    }

    private static async Task<ReanalyzeSourceRepositoryOutcome> PersistAsync(
        SourceRepository existing,
        AnalyzedProviderRepository analyzed,
        PersistReanalyzedSourceRepository persistReanalyzedSourceRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var selectedPlan = analyzed.Analysis.Candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.CandidateKey, existing.CandidateKey, StringComparison.Ordinal));
        if (selectedPlan is null)
        {
            return new ReanalyzeSourceRepositoryError(
                new SourceRepositorySelectedBuildPlanUnavailable(
                    existing.Id,
                    existing.CandidateKey));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = await persistReanalyzedSourceRepository(
            existing,
            analyzed.Repository,
            analyzed.Analysis,
            selectedPlan,
            now,
            cancellationToken);
        return updated switch
        {
            Ok<SourceRepository> success => success.Value.ToDto(),
            Err<SourceRepositoryNotFound> error => new ReanalyzeSourceRepositoryError(error.Value)
        };
    }
}

public sealed class ReanalyzeSourceRepositoryRequestValidator
    : AbstractValidator<ReanalyzeSourceRepositoryRequest>
{
    public ReanalyzeSourceRepositoryRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Source repository ID");
}

public static class ReanalyzeSourceRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapReanalyzeSourceRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/reanalyze", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] ReanalyzeSourceRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    SourceRepositoryDto repository => Results.Ok(repository),
                    ReanalyzeSourceRepositoryError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("ReanalyzeSourceRepository")
            .Produces<SourceRepositoryDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return app;
    }
}
