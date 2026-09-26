using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.GitProviders.Application.Common.GitProviderHttpMappings;

namespace Pole.Features.GitProviders.Application.Endpoints.Repositories;

public sealed record AnalyzeProviderRepositoryRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromTenantHeader] string TenantId,
    [FromBody] AnalyzeRepositoryRequestBody Body)
    : IMediatorRequest<AnalyzeProviderRepositoryOutcome>, ITenantReadOperation;

public union AnalyzeProviderRepositoryOutcome(
    RepositoryAnalysisDto,
    AnalyzeProviderRepositoryError);

public sealed class AnalyzeProviderRepositoryHandler(
    AnalyzeProviderRepository analyzeProviderRepository)
    : IMediatorOutcomeHandler<AnalyzeProviderRepositoryRequest, AnalyzeProviderRepositoryOutcome>
{
    public async Task<AnalyzeProviderRepositoryOutcome> HandleAsync(
        AnalyzeProviderRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var analyzed = await analyzeProviderRepository(
            request.TenantId,
            request.ConnectionId,
            request.RepositoryId,
            request.Body.Branch,
            cancellationToken);
        return analyzed switch
        {
            AnalyzedProviderRepository success => success.Analysis.ToDto(success.Repository),
            AnalyzeProviderRepositoryError error => error
        };
    }
}

public sealed class AnalyzeProviderRepositoryRequestValidator
    : AbstractValidator<AnalyzeProviderRepositoryRequest>
{
    public AnalyzeProviderRepositoryRequestValidator()
    {
        RuleFor(request => request.ConnectionId).MongoObjectId("Git provider connection ID");
        RuleFor(request => request.RepositoryId).NotEmpty();
        RuleFor(request => request.Body).NotNull();
        RuleFor(request => request.Body.Branch).NotEmpty();
    }
}

public static class AnalyzeProviderRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapAnalyzeProviderRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{ConnectionId}/repositories/{RepositoryId}/analysis", async (
                IMediator mediator,
                [AsParameters] AnalyzeProviderRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    RepositoryAnalysisDto analysis => Results.Ok(analysis),
                    AnalyzeProviderRepositoryError error => MapAnalyzeError(error)
                });
            })
            .WithName("AnalyzeProviderRepository")
            .Produces<RepositoryAnalysisDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return app;
    }
}
