using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Credentials;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record RotateDeploymentTargetCredentialsRequestBody(
    long Revision,
    DeploymentTargetConnectionRequestBody Connection
);

public sealed record RotateDeploymentTargetCredentialsRequest(
    [FromRoute] string Id,
    [FromBody] RotateDeploymentTargetCredentialsRequestBody Body
) : IMediatorRequest<RotateDeploymentTargetCredentialsOutcome>;

public union RotateDeploymentTargetCredentialsOutcome(
    DeploymentTargetDto,
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict,
    DeploymentTargetConnectionRejected);

public sealed class RotateDeploymentTargetCredentialsHandler(
    LoadDeploymentTargetById loadDeploymentTargetById,
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    PersistRotatedDeploymentTargetConnection persistRotatedDeploymentTargetConnection,
    PrepareDeploymentTargetConnection prepareDeploymentTargetConnection,
    ProbeDeploymentTargetConnection probeDeploymentTargetConnection,
    InvalidateDeploymentTargetKubernetesClient invalidateDeploymentTargetKubernetesClient,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<RotateDeploymentTargetCredentialsRequest, RotateDeploymentTargetCredentialsOutcome>
{
    public async Task<RotateDeploymentTargetCredentialsOutcome> HandleAsync(
        RotateDeploymentTargetCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await loadDeploymentTargetById(request.Id, cancellationToken);
        if (target is null)
            return new DeploymentTargetNotFound(request.Id);
        if (target.Revision != request.Body.Revision)
            return new DeploymentTargetRevisionConflict(target.Id);

        var preparedResult = await prepareDeploymentTargetConnection(
            target.Id,
            target.NextConnectionVersion,
            request.Body.Connection.ToInput(),
            cancellationToken);
        if (preparedResult is DeploymentTargetConnectionRejected prepareError)
            return prepareError;
        var prepared = (PreparedDeploymentTargetConnection)preparedResult.Value;

        using (prepared)
        {
            var observation = await probeDeploymentTargetConnection(
                prepared.Candidate,
                prepared.Connection.Version,
                target.Health.LastHealthyAtUtc,
                cancellationToken);
            if (!observation.IsUsable || observation.ClusterIdentity is null)
                return ConnectionValidationFailure(
                    observation.Health.FailureMessage ?? "The Kubernetes connection is not usable.");
            if (!string.Equals(target.ClusterIdentity, observation.ClusterIdentity, StringComparison.Ordinal))
                return ConnectionValidationFailure(
                    "Replacement credentials resolve to a different Kubernetes cluster.");

            var credentialReplaced = target.ReplaceConnectionCredential(
                observation.ClusterIdentity,
                prepared.Connection,
                DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc));
            var updated = credentialReplaced.ApplyValidationObservation(
                credentialReplaced.Revision,
                prepared.Connection.Version,
                observation.Health,
                observation.Capabilities);
            var persistedResult = await persistRotatedDeploymentTargetConnection(target, updated, cancellationToken);
            if (persistedResult is DeploymentTargetRevisionConflict persistenceError)
                return persistenceError;

            var persisted = (DeploymentTarget)persistedResult.Value;
            invalidateDeploymentTargetKubernetesClient(target.Id);
            var defaultId = await loadDefaultDeploymentTargetId(cancellationToken);
            return persisted.ToDto(persisted.Id == defaultId);
        }
    }

    private static DeploymentTargetConnectionRejected ConnectionValidationFailure(string safeMessage) => new(
        new Dictionary<string, string[]>
        {
            ["Body.Connection"] = [safeMessage],
        });
}

public sealed class RotateDeploymentTargetCredentialsRequestValidator
    : AbstractValidator<RotateDeploymentTargetCredentialsRequest>
{
    public RotateDeploymentTargetCredentialsRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Deployment target ID");
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new RotateDeploymentTargetCredentialsRequestBodyValidator());
    }
}

public sealed class RotateDeploymentTargetCredentialsRequestBodyValidator
    : AbstractValidator<RotateDeploymentTargetCredentialsRequestBody>
{
    public RotateDeploymentTargetCredentialsRequestBodyValidator()
    {
        RuleFor(body => body.Revision).GreaterThan(0);
        RuleFor(body => body.Connection).NotNull().SetValidator(new DeploymentTargetConnectionRequestBodyValidator());
    }
}

public static class RotateDeploymentTargetCredentialsEndpoint
{
    public static IEndpointRouteBuilder MapRotateDeploymentTargetCredentialsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut(
                "/{Id}/credentials",
                async (
                    IMediator mediator, [AsParameters] RotateDeploymentTargetCredentialsRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        DeploymentTargetDto target => Results.Ok(target),
                        DeploymentTargetNotFound error => DeploymentTargetHttpMappings.Map(error),
                        DeploymentTargetRevisionConflict error => DeploymentTargetHttpMappings.Map(error),
                        DeploymentTargetConnectionRejected error => DeploymentTargetHttpMappings.Map(error),
                    });
                })
            .WithName("RotateDeploymentTargetCredentials")
            .Produces<DeploymentTargetDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }
}
