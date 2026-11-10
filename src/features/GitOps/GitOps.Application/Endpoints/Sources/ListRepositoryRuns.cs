using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Sources;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

public sealed record ListRepositoryRunsRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 50
) : IMediatorRequest<ListRepositoryRunsOutcome>, ITenantReadOperation;

public union ListRepositoryRunsOutcome(RepositoryRunPage, GitSourceNotFound);

public sealed class ListRepositoryRunsValidator : AbstractValidator<ListRepositoryRunsRequest>
{
    public ListRepositoryRunsValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Page).InclusiveBetween(1, 1000000);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListRepositoryRunsHandler(
    GitRepositoryQueries queries
) : IMediatorOutcomeHandler<ListRepositoryRunsRequest, ListRepositoryRunsOutcome>
{
    public async Task<ListRepositoryRunsOutcome> HandleAsync(
        ListRepositoryRunsRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();
        var sourceId = ObjectId.Parse(request.Id).ToString();

        var result = await queries.RunsAsync(tenantId, sourceId, request.Page, request.PageSize, cancellationToken);
        return result is null ? new GitSourceNotFound() : result;
    }
}

public static class ListRepositoryRunsEndpoint
{
    public static IEndpointRouteBuilder MapListRepositoryRunsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/runs",
                async (
                    IMediator mediator, [AsParameters] ListRepositoryRunsRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    RepositoryRunPage value => Results.Ok(value),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing),
                }))
            .WithName("ListRepositoryRuns");
        return app;
    }
}
