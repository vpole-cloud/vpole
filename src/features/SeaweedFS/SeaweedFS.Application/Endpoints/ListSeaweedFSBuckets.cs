using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.SeaweedFS.Application.Endpoints;

using Pole.Features.SeaweedFS.Domain;

public sealed record SeaweedFSBucketList(
    IReadOnlyList<SeaweedFSBucketDto> Items
);

public union ListSeaweedFSBucketsOutcome(SeaweedFSBucketList, SeaweedFSNotFound);

public sealed record ListSeaweedFSBucketsRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<ListSeaweedFSBucketsOutcome>, ITenantReadOperation;

public sealed class ListSeaweedFSBucketsValidator : AbstractValidator<ListSeaweedFSBucketsRequest>
{
    public ListSeaweedFSBucketsValidator() => RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
}

public sealed class ListSeaweedFSBucketsHandler(
    LoadSeaweedFSById load,
    LoadSeaweedFSBuckets buckets
) : IMediatorOutcomeHandler<ListSeaweedFSBucketsRequest, ListSeaweedFSBucketsOutcome>
{
    public async Task<ListSeaweedFSBucketsOutcome> HandleAsync(
        ListSeaweedFSBucketsRequest request,
        CancellationToken cancellationToken = default)
    {
        var service = await load(request.TenantId, request.Id, cancellationToken);
        if (service is null || service.Lifecycle is DeletedSeaweedFSLifecycle)
            return new SeaweedFSNotFound();
        return new SeaweedFSBucketList(
            (await buckets(request.TenantId, request.Id, cancellationToken))
            .Where(x => x.Lifecycle is not DeletedSeaweedFSLifecycle)
            .Select(x => x.ToDto())
            .ToArray());
    }
}

public static class ListSeaweedFSBucketsEndpoint
{
    public static IEndpointRouteBuilder MapListSeaweedFSBucketsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/buckets",
                async (
                    IMediator mediator, [AsParameters] ListSeaweedFSBucketsRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SeaweedFSBucketList values => Results.Ok(values.Items),
                    SeaweedFSNotFound => SeaweedFSHttpMappings.Problem(404, "not-found", "SeaweedFS resource not found")
                }))
            .WithName("ListSeaweedFSBuckets");
        return app;
    }
}
