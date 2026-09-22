using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.Secrets.Application.Endpoints;

public sealed record SecretProjectionStatusDto(
    string ResourceDeploymentId,
    string BindingId,
    string DeploymentTargetId,
    long DesiredVersion,
    long? AppliedVersion,
    string Health,
    DateTime? LastAttemptAtUtc,
    string? FailureCode)
{
    public static SecretProjectionStatusDto From(SecretProjection projection) => new(
        projection.DeploymentId,
        projection.BindingId,
        projection.DeploymentTargetId,
        projection.DesiredVersion,
        projection.AppliedVersion,
        projection.State switch
        {
            PendingSecretProjection => "Pending",
            ReadySecretProjection => "Ready",
            FailedSecretProjection => "Failed",
            DeletedSecretProjection => "Deleted",
            _ => throw new InvalidOperationException("Unknown Secret projection state.")
        },
        projection.LastAttemptAtUtc,
        (projection.State as FailedSecretProjection)?.FailureCode);
}

public union GetSecretProjectionsOutcome(
    SecretProjectionStatusDto[],
    SecretMissing);

public sealed record GetSecretProjectionsRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<GetSecretProjectionsOutcome>, ITenantReadOperation;

public sealed class GetSecretProjectionsValidator : AbstractValidator<GetSecretProjectionsRequest>
{
    public GetSecretProjectionsValidator() => RuleFor(x => x.Id).MongoObjectId("Secret ID");
}

public sealed class GetSecretProjectionsHandler(
    LoadSecretById loadSecret,
    LoadSecretProjections loadProjections)
    : IMediatorOutcomeHandler<GetSecretProjectionsRequest, GetSecretProjectionsOutcome>
{
    public async Task<GetSecretProjectionsOutcome> HandleAsync(GetSecretProjectionsRequest request, CancellationToken cancellationToken = default)
    {
        var secret = await loadSecret(request.TenantId, request.Id, cancellationToken);
        if (secret is null)
            return new SecretMissing();
        return (await loadProjections(secret.TenantId, secret.Id, cancellationToken))
            .Select(SecretProjectionStatusDto.From).ToArray();
    }
}

public static class GetSecretProjectionsEndpoint
{
    public static IEndpointRouteBuilder MapGetSecretProjectionsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/projections", async (
                IMediator mediator,
                IHttpProblemMapper problems,
                [AsParameters] GetSecretProjectionsRequest request,
                CancellationToken ct) =>
                (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SecretProjectionStatusDto[] projections => Results.Ok(projections),
                    SecretMissing missing => problems.Map(missing).ToHttpResult()
                }))
            .WithName("GetSecretProjections");
        return app;
    }
}
