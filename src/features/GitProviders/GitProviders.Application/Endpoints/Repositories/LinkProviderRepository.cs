using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence.Repositories;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.GitProviders.Application.Common.GitProviderHttpMappings;

namespace Pole.Features.GitProviders.Application.Endpoints.Repositories;

public sealed record LinkProviderRepositoryRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromTenantHeader] string TenantId,
    [FromBody] LinkSourceRepositoryRequestBody Body)
    : IMediatorRequest<LinkProviderRepositoryOutcome>, ITenantWriteOperation;

public union LinkProviderRepositoryOutcome(
    SourceRepositoryDto,
    AnalyzeProviderRepositoryError,
    SourceRepositoryBuildPlanNotFound,
    SourceRepositoryAlreadyLinked);

public sealed class LinkProviderRepositoryHandler(
    AnalyzeProviderRepository analyzeProviderRepository,
    PersistLinkedSourceRepository persistLinkedSourceRepository)
    : IMediatorOutcomeHandler<LinkProviderRepositoryRequest, LinkProviderRepositoryOutcome>
{
    public async Task<LinkProviderRepositoryOutcome> HandleAsync(
        LinkProviderRepositoryRequest request,
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
            AnalyzeProviderRepositoryError error => error,
            AnalyzedProviderRepository success => await PersistAsync(
                request,
                success,
                persistLinkedSourceRepository,
                cancellationToken)
        };
    }

    private static async Task<LinkProviderRepositoryOutcome> PersistAsync(
        LinkProviderRepositoryRequest request,
        AnalyzedProviderRepository analyzed,
        PersistLinkedSourceRepository persistLinkedSourceRepository,
        CancellationToken cancellationToken)
    {
        var selectedPlan = analyzed.Analysis.Candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.CandidateKey, request.Body.CandidateKey, StringComparison.Ordinal));
        if (selectedPlan is null)
        {
            return new SourceRepositoryBuildPlanNotFound(request.Body.CandidateKey);
        }

        var sourceRepository = SourceRepositoryFactory.Create(
            request.TenantId, request.ConnectionId, request.RepositoryId, analyzed.Repository.FullName,
            analyzed.Repository.HtmlUrl, analyzed.Repository.IsPrivate, analyzed.Analysis, selectedPlan,
            SourceRepositoryFactory.DefaultOptions);
        var inserted = await persistLinkedSourceRepository(
            sourceRepository,
            cancellationToken);
        return inserted switch
        {
            SourceRepository success => success.ToDto(),
            SourceRepositoryAlreadyLinked error => error
        };
    }
}

public sealed class LinkProviderRepositoryRequestValidator
    : AbstractValidator<LinkProviderRepositoryRequest>
{
    public LinkProviderRepositoryRequestValidator()
    {
        RuleFor(request => request.ConnectionId).MongoObjectId("Git provider connection ID");
        RuleFor(request => request.RepositoryId).NotEmpty();
        RuleFor(request => request.Body).NotNull();
        RuleFor(request => request.Body.Branch).NotEmpty();
        RuleFor(request => request.Body.CandidateKey).NotEmpty();
    }
}

public static class LinkProviderRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapLinkProviderRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{ConnectionId}/repositories/{RepositoryId}/links", async (
                IMediator mediator,
                [AsParameters] LinkProviderRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    SourceRepositoryDto repository => Results.Created(
                        $"/api/v1/source-repositories/{repository.Id}",
                        repository),
                    AnalyzeProviderRepositoryError error => MapAnalyzeError(error),
                    SourceRepositoryBuildPlanNotFound error => MapBuildPlanNotFound(error),
                    SourceRepositoryAlreadyLinked error => MapSourceRepositoryAlreadyLinked(error)
                });
            })
            .WithName("LinkProviderRepository")
            .Produces<SourceRepositoryDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return app;
    }
}
