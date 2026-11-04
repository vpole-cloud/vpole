using MongoDB.Bson;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Sources.Mongo;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

public sealed record ListRepositoryOperationsRequest(
    [FromRoute] string Id,
    [FromRoute] string RunId,
    [FromTenantHeader] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 50)
    : IMediatorRequest<ListRepositoryOperationsOutcome>, ITenantReadOperation;

public union ListRepositoryOperationsOutcome(
    RepositoryOperationPage,
    GitSourceNotFound);

public sealed class ListRepositoryOperationsValidator : AbstractValidator<ListRepositoryOperationsRequest>
{
    public ListRepositoryOperationsValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.RunId).MongoObjectId("Run ID");
        RuleFor(item => item.Page).InclusiveBetween(1, 1000000);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListRepositoryOperationsHandler(GitRepositoryQueries queries)
    : IMediatorOutcomeHandler<ListRepositoryOperationsRequest, ListRepositoryOperationsOutcome>
{
    public async Task<ListRepositoryOperationsOutcome> HandleAsync(
        ListRepositoryOperationsRequest request,
        CancellationToken cancellationToken = default)
    {
        var tenantId = ObjectId.Parse(request.TenantId).ToString();
        var sourceId = ObjectId.Parse(request.Id).ToString();
        var runId = ObjectId.Parse(request.RunId).ToString();

        var result = await queries.OperationsAsync(tenantId,
            sourceId,
            runId,
            request.Page,
            request.PageSize,
            cancellationToken);
        return result is null ? new GitSourceNotFound() : result;
    }
}

public static class ListRepositoryOperationsEndpoint
{
    public static IEndpointRouteBuilder MapListRepositoryOperationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/runs/{RunId}/operations",
            async (IMediator mediator,
                    [AsParameters] ListRepositoryOperationsRequest request, CancellationToken ct) =>
                (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    RepositoryOperationPage value => Results.Ok(value),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing)
                })).WithName("ListRepositoryOperations");
        return app;
    }
}
