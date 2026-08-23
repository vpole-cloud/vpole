using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record RotateDeploymentTargetCredentialsRequestBody(
    long Revision,
    DeploymentTargetConnectionRequestBody Connection);

public sealed record RotateDeploymentTargetCredentialsRequest(
    [FromRoute] string Id,
    [FromBody] RotateDeploymentTargetCredentialsRequestBody Body)
    : IMediatorRequest<RotateDeploymentTargetCredentialsOutcome>;

public union RotateDeploymentTargetCredentialsError(
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict,
    DeploymentTargetConnectionRejected);

public union RotateDeploymentTargetCredentialsOutcome(
    DeploymentTargetDto,
    RotateDeploymentTargetCredentialsError);

public sealed class RotateDeploymentTargetCredentialsHandler(
    LoadDeploymentTargetById loadDeploymentTargetById,
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    PersistRotatedDeploymentTargetConnection persistRotatedDeploymentTargetConnection,
    PrepareDeploymentTargetConnection prepareDeploymentTargetConnection,
    ProbeDeploymentTargetConnection probeDeploymentTargetConnection,
    InvalidateDeploymentTargetKubernetesClient invalidateDeploymentTargetKubernetesClient,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        RotateDeploymentTargetCredentialsRequest,
        RotateDeploymentTargetCredentialsOutcome>
{
    public async Task<RotateDeploymentTargetCredentialsOutcome> HandleAsync(
        RotateDeploymentTargetCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await loadDeploymentTargetById(request.Id, cancellationToken);
        if (target is null)
            return Error(new DeploymentTargetNotFound(request.Id));
        if (target.Revision != request.Body.Revision)
            return Error(new DeploymentTargetRevisionConflict(target.Id));

        var preparedResult = await prepareDeploymentTargetConnection(
            target.Id,
            target.NextConnectionVersion,
            request.Body.Connection.ToInput(),
            cancellationToken);
        if (!preparedResult.TryGetValue(out var prepared, out var prepareError))
            return Error(prepareError);

        using (prepared)
        {
            var observation = await probeDeploymentTargetConnection(
                prepared.Candidate,
                prepared.Connection.Version,
                target.Health.LastHealthyAtUtc,
                cancellationToken);
            if (!observation.IsUsable || observation.ClusterIdentity is null)
            {
                return Error(ConnectionValidationFailure(
                    observation.Health.FailureMessage ?? "The Kubernetes connection is not usable."));
            }
            if (!string.Equals(target.ClusterIdentity, observation.ClusterIdentity, StringComparison.Ordinal))
            {
                return Error(ConnectionValidationFailure(
                    "Replacement credentials resolve to a different Kubernetes cluster."));
            }

            var credentialReplaced = target.ReplaceConnectionCredential(
                observation.ClusterIdentity,
                prepared.Connection,
                DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc));
            var updated = credentialReplaced.ApplyValidationObservation(
                credentialReplaced.Revision,
                prepared.Connection.Version,
                observation.Health,
                observation.Capabilities);
            var persistedResult = await persistRotatedDeploymentTargetConnection(
                target,
                updated,
                cancellationToken);
            if (!persistedResult.TryGetValue(out var persisted, out var persistenceError))
                return Error(persistenceError);

            invalidateDeploymentTargetKubernetesClient(target.Id);
            var defaultId = await loadDefaultDeploymentTargetId(cancellationToken);
            return persisted.ToDto(persisted.Id == defaultId);
        }
    }

    private static DeploymentTargetConnectionRejected ConnectionValidationFailure(string safeMessage) =>
        new(new Dictionary<string, string[]>
        {
            ["Body.Connection"] = [safeMessage]
        });

    private static RotateDeploymentTargetCredentialsOutcome Error(
        DeploymentTargetNotFound error) =>
        new(new RotateDeploymentTargetCredentialsError(error));

    private static RotateDeploymentTargetCredentialsOutcome Error(
        DeploymentTargetRevisionConflict error) =>
        new(new RotateDeploymentTargetCredentialsError(error));

    private static RotateDeploymentTargetCredentialsOutcome Error(
        DeploymentTargetConnectionRejected error) =>
        new(new RotateDeploymentTargetCredentialsError(error));
}

public sealed class RotateDeploymentTargetCredentialsRequestValidator
    : AbstractValidator<RotateDeploymentTargetCredentialsRequest>
{
    public RotateDeploymentTargetCredentialsRequestValidator()
    {
        RuleFor(request => request.Id)
            .MongoObjectId("Deployment target ID");
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
        RuleFor(body => body.Connection)
            .NotNull()
            .SetValidator(new DeploymentTargetConnectionRequestBodyValidator());
    }
}

public static class RotateDeploymentTargetCredentialsEndpoint
{
    public static IEndpointRouteBuilder MapRotateDeploymentTargetCredentialsEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPut("/{Id}/credentials", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] RotateDeploymentTargetCredentialsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    DeploymentTargetDto target => Results.Ok(target),
                    RotateDeploymentTargetCredentialsError error =>
                        httpProblems.Map(error).ToHttpResult()
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
