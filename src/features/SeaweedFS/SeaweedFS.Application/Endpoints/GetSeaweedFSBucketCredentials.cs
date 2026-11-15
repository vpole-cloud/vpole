using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.SeaweedFS.Application.Endpoints;

public union GetSeaweedFSBucketCredentialsOutcome(
    SeaweedFSCredentialsDto,
    SeaweedFSNotFound,
    SeaweedFSForbidden,
    SeaweedFSCredentialFailure);

public sealed record GetSeaweedFSBucketCredentialsRequest(
    [FromRoute] string Id,
    [FromRoute] string BucketId,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue ActorUserId
) : IMediatorRequest<GetSeaweedFSBucketCredentialsOutcome>, ITenantWriteOperation;

public sealed class GetSeaweedFSBucketCredentialsValidator : AbstractValidator<GetSeaweedFSBucketCredentialsRequest>
{
    public GetSeaweedFSBucketCredentialsValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
        RuleFor(x => x.BucketId).MongoObjectId("Bucket ID");
    }
}

public sealed class GetSeaweedFSBucketCredentialsHandler(
    LoadSeaweedFSById load,
    ReadManagedSecret read,
    LoadSeaweedFSBuckets buckets,
    LoadTenantMember loadTenantMember,
    SeaweedFSAudit audit
) : IMediatorOutcomeHandler<GetSeaweedFSBucketCredentialsRequest, GetSeaweedFSBucketCredentialsOutcome>
{
    public async Task<GetSeaweedFSBucketCredentialsOutcome> HandleAsync(
        GetSeaweedFSBucketCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        var service = await load(request.TenantId, request.Id, cancellationToken);
        if (service is null || service.Lifecycle is not ActiveSeaweedFSLifecycle)
            return new SeaweedFSNotFound();
        if (await loadTenantMember(
                new(request.TenantId, request.ActorUserId.Value),
                cancellationToken: cancellationToken) is not TenantMemberSnapshot
            {
                Role: >= TenantMembershipRole.Member
            })
            return new SeaweedFSForbidden();
        var bucket = (await buckets(request.TenantId, request.Id, cancellationToken)).SingleOrDefault(x =>
            x.Id == request.BucketId);
        if (bucket is null || bucket.Lifecycle is not ActiveSeaweedFSLifecycle)
            return new SeaweedFSNotFound();
        var owner = SeaweedFSCredentials.BucketOwner(request.Id, request.BucketId);
        var result = await read(request.TenantId, owner, cancellationToken);
        if (result is not ManagedSecretValues values)
            return new SeaweedFSCredentialFailure();
        if (!values.Values.TryGetValue(SeaweedFSCredentials.AccessKey, out var access) ||
            !values.Values.TryGetValue(SeaweedFSCredentials.SecretKey, out var secret))
            return new SeaweedFSCredentialFailure();
        if (!await audit.RevealAsync(
                request.TenantId,
                request.Id,
                request.BucketId,
                bucket.Revision,
                request.ActorUserId.Value,
                cancellationToken))
            return new SeaweedFSNotFound();
        return new SeaweedFSCredentialsDto(access, secret);
    }
}

public static class GetSeaweedFSBucketCredentialsEndpoint
{
    public static IEndpointRouteBuilder MapGetSeaweedFSBucketCredentialsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/buckets/{BucketId}/credentials",
                async (
                    IMediator mediator, [AsParameters] GetSeaweedFSBucketCredentialsRequest request,
                    HttpContext context, CancellationToken ct) =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    return (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                    {
                        SeaweedFSCredentialsDto dto => Results.Ok(dto),
                        SeaweedFSNotFound => SeaweedFSHttpMappings.Problem(
                            404,
                            "not-found",
                            "SeaweedFS resource not found"),
                        SeaweedFSForbidden => SeaweedFSHttpMappings.Problem(
                            403,
                            "member-required",
                            "Tenant member permission required"),
                        SeaweedFSCredentialFailure => SeaweedFSHttpMappings.Problem(
                            503,
                            "credentials-unavailable",
                            "Credentials unavailable")
                    });
                })
            .WithName("GetSeaweedFSBucketCredentials");
        return app;
    }
}
