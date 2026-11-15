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

public sealed record RotateSeaweedFSBucketCredentialsBody(
    long ExpectedRevision
);

public union RotateSeaweedFSBucketCredentialsOutcome(
    SeaweedFSBucketDto,
    SeaweedFSNotFound,
    SeaweedFSConflict,
    SeaweedFSCredentialFailure);

public sealed record RotateSeaweedFSBucketCredentialsRequest(
    [FromRoute] string Id,
    [FromRoute] string BucketId,
    [FromBody] RotateSeaweedFSBucketCredentialsBody Body,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<RotateSeaweedFSBucketCredentialsOutcome>, ITenantWriteOperation;

public sealed class RotateSeaweedFSBucketCredentialsValidator
    : AbstractValidator<RotateSeaweedFSBucketCredentialsRequest>
{
    public RotateSeaweedFSBucketCredentialsValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
        RuleFor(x => x.BucketId).MongoObjectId("Bucket ID");
        RuleFor(x => x.Body).NotNull().DependentRules(() => { RuleFor(x => x.Body.ExpectedRevision).GreaterThan(0); });
    }
}

public sealed class RotateSeaweedFSBucketCredentialsHandler(
    SeaweedFSMutations mutations
) : IMediatorOutcomeHandler<RotateSeaweedFSBucketCredentialsRequest, RotateSeaweedFSBucketCredentialsOutcome>
{
    public async Task<RotateSeaweedFSBucketCredentialsOutcome> HandleAsync(
        RotateSeaweedFSBucketCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await mutations.RotateBucketAsync(
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
            SeaweedFSCredentialFailure value => value,
            SeaweedFSServiceDto => throw new InvalidOperationException("Unexpected resource type in mutation result.")
        };
    }
}

public static class RotateSeaweedFSBucketCredentialsEndpoint
{
    public static IEndpointRouteBuilder MapRotateSeaweedFSBucketCredentialsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{Id}/buckets/{BucketId}/rotate-credentials",
                async (
                    IMediator mediator, [AsParameters] RotateSeaweedFSBucketCredentialsRequest request,
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
                        "SeaweedFS resource changed or operation unavailable"),
                    SeaweedFSCredentialFailure => SeaweedFSHttpMappings.Problem(
                        503,
                        "credentials-unavailable",
                        "Credential operation unavailable")
                }))
            .WithName("RotateSeaweedFSBucketCredentials");
        return app;
    }
}
