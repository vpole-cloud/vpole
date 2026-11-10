using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Application.Repositories;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.GitProviders.Application.Common.GitProviderHttpMappings;

namespace Pole.Features.GitProviders.Application.Endpoints.SourceRepositories;

public sealed record ReanalyzeSourceRepositoryRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<ReanalyzeSourceRepositoryOutcome>, ITenantWriteOperation;

public union ReanalyzeSourceRepositoryOutcome(
    RepositoryAnalysisDto,
    SourceRepositoryNotFound,
    AnalyzeProviderRepositoryError);

public sealed class ReanalyzeSourceRepositoryHandler(
    LoadSourceRepository loadSourceRepository,
    AnalyzeProviderRepository analyzeProviderRepository
) : IMediatorOutcomeHandler<ReanalyzeSourceRepositoryRequest, ReanalyzeSourceRepositoryOutcome>
{
    public async Task<ReanalyzeSourceRepositoryOutcome> HandleAsync(
        ReanalyzeSourceRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await loadSourceRepository(request.TenantId, request.Id, cancellationToken);
        if (existing is null)
            return new SourceRepositoryNotFound(request.Id);

        var analyzed = await analyzeProviderRepository(
            request.TenantId,
            existing.ConnectionId,
            existing.ExternalRepositoryId,
            existing.Branch,
            cancellationToken);
        return analyzed switch
        {
            AnalyzeProviderRepositoryError error => error,
            AnalyzedProviderRepository success => success.Analysis.ToDto(success.Repository),
        };
    }
}

public sealed class ReanalyzeSourceRepositoryRequestValidator : AbstractValidator<ReanalyzeSourceRepositoryRequest>
{
    public ReanalyzeSourceRepositoryRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Source repository ID");
}

public static class ReanalyzeSourceRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapReanalyzeSourceRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{Id}/reanalyze",
                async (
                    IMediator mediator, [AsParameters] ReanalyzeSourceRepositoryRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        RepositoryAnalysisDto analysis => Results.Ok(analysis),
                        SourceRepositoryNotFound error => MapSourceRepositoryNotFound(error),
                        AnalyzeProviderRepositoryError error => MapAnalyzeError(error),
                    });
                })
            .WithName("ReanalyzeSourceRepository")
            .Produces<RepositoryAnalysisDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return app;
    }
}
