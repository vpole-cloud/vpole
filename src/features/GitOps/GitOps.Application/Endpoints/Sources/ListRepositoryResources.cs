using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Primitives;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints.Sources;

public sealed record RepositoryResourceView(
    string Kind,
    string Name,
    string ResourceId,
    string? ResourceTypeId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GitResourceMaterialization>))]
    GitResourceMaterialization Materialization,
    bool PendingRemoval,
    string? Blocker,
    string LastRunId);

public sealed record RepositoryResourcePage(
    IReadOnlyList<RepositoryResourceView> Items,
    int Page,
    int PageSize,
    bool HasMore,
    long InventoryRevision);

public sealed record ListRepositoryResourcesRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 50) : IMediatorRequest<ListRepositoryResourcesOutcome>, ITenantReadOperation;

public union ListRepositoryResourcesOutcome(
    RepositoryResourcePage,
    GitSourceNotFound);

public sealed class ListRepositoryResourcesValidator : AbstractValidator<ListRepositoryResourcesRequest>
{
    public ListRepositoryResourcesValidator()
    {
        RuleFor(item => item.Id).MongoObjectId("Source ID");
        RuleFor(item => item.Page).InclusiveBetween(1, 1000000);
        RuleFor(item => item.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class ListRepositoryResourcesHandler(GitRepositoryInventory inventory)
    : IMediatorOutcomeHandler<ListRepositoryResourcesRequest, ListRepositoryResourcesOutcome>
{
    public Task<ListRepositoryResourcesOutcome> HandleAsync(ListRepositoryResourcesRequest request,
        CancellationToken cancellationToken = default) => inventory.ReadAsync(new(
        ObjectId.Parse(request.TenantId).ToString(), ObjectId.Parse(request.Id).ToString(),
        request.Page, request.PageSize), cancellationToken);
}

public static class ListRepositoryResourcesEndpoint
{
    public static IEndpointRouteBuilder MapListRepositoryResourcesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/resources", async (IMediator mediator,
                [AsParameters] ListRepositoryResourcesRequest request, CancellationToken ct) =>
                (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    RepositoryResourcePage page => Results.Ok(page),
                    GitSourceNotFound missing => RepositoryHttpMappings.Map(missing)
                })).WithName("ListRepositoryResources");
        return app;
    }
}
