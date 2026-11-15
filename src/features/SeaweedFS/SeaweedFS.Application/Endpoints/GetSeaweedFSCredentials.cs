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

public union GetSeaweedFSCredentialsOutcome(
    SeaweedFSCredentialsDto,
    SeaweedFSNotFound,
    SeaweedFSForbidden,
    SeaweedFSCredentialFailure);

public sealed record GetSeaweedFSCredentialsRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue ActorUserId
) : IMediatorRequest<GetSeaweedFSCredentialsOutcome>, ITenantWriteOperation;

public sealed class GetSeaweedFSCredentialsValidator : AbstractValidator<GetSeaweedFSCredentialsRequest>
{
    public GetSeaweedFSCredentialsValidator()
    {
        RuleFor(x => x.Id).MongoObjectId("SeaweedFS ID");
    }
}

public sealed class GetSeaweedFSCredentialsHandler(
    LoadSeaweedFSById load,
    ReadManagedSecret read,
    LoadTenantMember loadTenantMember,
    SeaweedFSAudit audit
) : IMediatorOutcomeHandler<GetSeaweedFSCredentialsRequest, GetSeaweedFSCredentialsOutcome>
{
    public async Task<GetSeaweedFSCredentialsOutcome> HandleAsync(
        GetSeaweedFSCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        var service = await load(request.TenantId, request.Id, cancellationToken);
        if (service is null || service.Lifecycle is not ActiveSeaweedFSLifecycle)
            return new SeaweedFSNotFound();
        if (await loadTenantMember(
                new(request.TenantId, request.ActorUserId.Value),
                cancellationToken: cancellationToken) is not TenantMemberSnapshot
            {
                Role: TenantMembershipRole.Owner
            })
            return new SeaweedFSForbidden();
        var owner = SeaweedFSCredentials.AdminOwner(request.Id);
        var result = await read(request.TenantId, owner, cancellationToken);
        if (result is not ManagedSecretValues values)
            return new SeaweedFSCredentialFailure();
        if (!values.Values.TryGetValue(SeaweedFSCredentials.AccessKey, out var access) ||
            !values.Values.TryGetValue(SeaweedFSCredentials.SecretKey, out var secret))
            return new SeaweedFSCredentialFailure();
        if (!await audit.RevealAsync(
                request.TenantId,
                request.Id,
                null,
                service.Revision,
                request.ActorUserId.Value,
                cancellationToken))
            return new SeaweedFSNotFound();
        return new SeaweedFSCredentialsDto(access, secret);
    }
}

public static class GetSeaweedFSCredentialsEndpoint
{
    public static IEndpointRouteBuilder MapGetSeaweedFSCredentialsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/credentials",
                async (
                    IMediator mediator, [AsParameters] GetSeaweedFSCredentialsRequest request,
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
                            "admin-required",
                            "Tenant admin permission required"),
                        SeaweedFSCredentialFailure => SeaweedFSHttpMappings.Problem(
                            503,
                            "credentials-unavailable",
                            "Credentials unavailable")
                    });
                })
            .WithName("GetSeaweedFSCredentials");
        return app;
    }
}
