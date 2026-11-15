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

public sealed record RestoreSeaweedFSBucketBody(
    long ExpectedRevision
);

public union RestoreSeaweedFSBucketOutcome(
    SeaweedFSBucketDto,
    SeaweedFSNotFound,
    SeaweedFSConflict);

public sealed record RestoreSeaweedFSBucketRequest(
    [FromRoute] string Id,
    [FromRoute] string BucketId,
    [FromBody] RestoreSeaweedFSBucketBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<RestoreSeaweedFSBucketOutcome>, ITenantWriteOperation;

public sealed class RestoreSeaweedFSBucketValidator : AbstractValidator<RestoreSeaweedFSBucketRequest>
{
    public RestoreSeaweedFSBucketValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
        RuleFor(x => x.BucketId).MongoObjectId("Bucket ID");
        RuleFor(x => x.Body).NotNull().DependentRules(() => { RuleFor(x => x.Body.ExpectedRevision).GreaterThan(0); });
    }
}

public sealed class RestoreSeaweedFSBucketHandler(
    SeaweedFSMutations mutations
) : IMediatorOutcomeHandler<RestoreSeaweedFSBucketRequest, RestoreSeaweedFSBucketOutcome>
{
    public async Task<RestoreSeaweedFSBucketOutcome> HandleAsync(
        RestoreSeaweedFSBucketRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await mutations.RestoreBucketAsync(
            request.TenantId,
            request.Id,
            request.BucketId,
            request.Body.ExpectedRevision,
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

public static class RestoreSeaweedFSBucketEndpoint
{
    public static IEndpointRouteBuilder MapRestoreSeaweedFSBucketEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{Id}/buckets/{BucketId}/restore",
                async (
                    IMediator mediator, [AsParameters] RestoreSeaweedFSBucketRequest request,
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
            .WithName("RestoreSeaweedFSBucket");
        return app;
    }
}
