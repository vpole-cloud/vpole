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

public union DeleteSeaweedFSBucketOutcome(
    SeaweedFSBucketDto,
    SeaweedFSNotFound,
    SeaweedFSConflict);

public sealed record DeleteSeaweedFSBucketRequest(
    [FromRoute] string Id,
    [FromRoute] string BucketId,
    [FromQuery] long ExpectedRevision,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<DeleteSeaweedFSBucketOutcome>, ITenantWriteOperation;

public sealed class DeleteSeaweedFSBucketValidator : AbstractValidator<DeleteSeaweedFSBucketRequest>
{
    public DeleteSeaweedFSBucketValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
        RuleFor(x => x.BucketId).MongoObjectId("Bucket ID");
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class DeleteSeaweedFSBucketHandler(
    SeaweedFSMutations mutations
) : IMediatorOutcomeHandler<DeleteSeaweedFSBucketRequest, DeleteSeaweedFSBucketOutcome>
{
    public async Task<DeleteSeaweedFSBucketOutcome> HandleAsync(
        DeleteSeaweedFSBucketRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await mutations.DeleteBucketAsync(
            request.TenantId,
            request.Id,
            request.BucketId,
            request.ExpectedRevision,
            cancellationToken);
        return result switch
        {
            SeaweedFSBucketDto value => value,
            SeaweedFSNotFound value => value,
            SeaweedFSConflict value => value,
            SeaweedFSCredentialFailure => throw new InvalidOperationException(
                "Unexpected credential operation in resource mutation."),
            SeaweedFSServiceDto => throw new InvalidOperationException("Unexpected resource type in mutation result.")
        };
    }
}

public static class DeleteSeaweedFSBucketEndpoint
{
    public static IEndpointRouteBuilder MapDeleteSeaweedFSBucketEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete(
                "/{Id}/buckets/{BucketId}",
                async (
                    IMediator mediator, [AsParameters] DeleteSeaweedFSBucketRequest request,
                    CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SeaweedFSBucketDto value => Results.Ok(value),
                    SeaweedFSNotFound => SeaweedFSHttpMappings.Problem(
                        404,
                        "not-found",
                        "SeaweedFS resource not found"),
                    SeaweedFSConflict error => SeaweedFSHttpMappings.Problem(
                        409,
                        error.Code,
                        "SeaweedFS resource changed or operation unavailable")
                }))
            .WithName("DeleteSeaweedFSBucket");
        return app;
    }
}
