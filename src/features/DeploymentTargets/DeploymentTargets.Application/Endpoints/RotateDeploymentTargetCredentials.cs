using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record RotateDeploymentTargetCredentialsRequestBody(
    long Revision,
    DeploymentTargetConnectionRequestBody Connection);

public sealed record RotateDeploymentTargetCredentialsRequest(
    string Id,
    RotateDeploymentTargetCredentialsRequestBody Body)
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class RotateDeploymentTargetCredentialsHandler(
    IDeploymentTargetDbContext dbContext,
    IDeploymentTargetConnectionPreparer connectionPreparer,
    IDeploymentTargetProbe probe,
    IDeploymentTargetClientCache clientCache,
    TimeProvider timeProvider)
    : IMediatorHandler<RotateDeploymentTargetCredentialsRequest, DeploymentTargetDto>
{
    public async Task<Result<DeploymentTargetDto>> HandleAsync(
        RotateDeploymentTargetCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Body is null || request.Body.Revision < 1 || request.Body.Connection is null)
            return DeploymentTargetEndpointSupport.BodyValidationFailure("Revision and connection are required.");
        var inputResult = DeploymentTargetEndpointSupport.ToConnectionInput(request.Body.Connection);
        if (!inputResult.TryGetValue(out var input, out var inputFailure))
            return inputFailure;
        var targetResult = await DeploymentTargetEndpointSupport.GetTargetAsync(
            dbContext,
            request.Id,
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;
        if (target.Revision != request.Body.Revision)
            return Failures.Conflict("The deployment target changed. Reload it and retry.");

        var nextConnectionVersion = checked(target.Connection.Version + 1);
        var preparedResult = await connectionPreparer.PrepareAsync(
            target.Id,
            nextConnectionVersion,
            input,
            cancellationToken);
        if (!preparedResult.TryGetValue(out var prepared, out var prepareFailure))
            return prepareFailure;

        using (prepared)
        {
            var observation = await probe.ProbeAsync(
                prepared.Candidate,
                prepared.Connection.Version,
                target.Health.LastHealthyAtUtc,
                cancellationToken);
            if (!observation.IsUsable || observation.ClusterIdentity is null)
            {
                return DeploymentTargetEndpointSupport.ConnectionValidationFailure(
                    observation.Health.FailureMessage ?? "The Kubernetes connection is not usable.");
            }
            if (!string.Equals(target.ClusterIdentity, observation.ClusterIdentity, StringComparison.Ordinal))
            {
                return DeploymentTargetEndpointSupport.ConnectionValidationFailure(
                    "Replacement credentials resolve to a different Kubernetes cluster.");
            }

            var updated = target.ReplaceConnectionCredential(
                    observation.ClusterIdentity,
                    prepared.Connection,
                    DeploymentTargetEndpointSupport.CheckedAtUtc(timeProvider, target.UpdatedAtUtc))
                .ApplyValidationObservation(
                    target.Revision + 1,
                    prepared.Connection.Version,
                    observation.Health,
                    observation.Capabilities);
            var persisted = await dbContext.DeploymentTargets.FindOneAndUpdateAsync(
                Builders<DeploymentTarget>.Filter.And(
                    DeploymentTargetEndpointSupport.RevisionFilter(target),
                    Builders<DeploymentTarget>.Filter.Eq(
                        item => item.Connection.Version,
                        target.Connection.Version)),
                Builders<DeploymentTarget>.Update.Combine(
                    Builders<DeploymentTarget>.Update.Set(item => item.Connection, updated.Connection),
                    Builders<DeploymentTarget>.Update.Set(item => item.Health, updated.Health),
                    Builders<DeploymentTarget>.Update.Set(item => item.Capabilities, updated.Capabilities),
                    Builders<DeploymentTarget>.Update.Set(item => item.Revision, updated.Revision),
                    Builders<DeploymentTarget>.Update.Set(item => item.UpdatedAtUtc, updated.UpdatedAtUtc)),
                new FindOneAndUpdateOptions<DeploymentTarget>
                {
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken);
            if (persisted is null)
                return Failures.Conflict("The deployment target changed. Reload it and retry.");

            clientCache.Invalidate(target.Id);
            var defaultId = await DeploymentTargetEndpointSupport.GetDefaultIdAsync(dbContext, cancellationToken);
            return persisted.ToDto(persisted.Id == defaultId);
        }
    }
}

public static class RotateDeploymentTargetCredentialsEndpoint
{
    public static IEndpointRouteBuilder MapRotateDeploymentTargetCredentialsEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPut("/{id}/credentials", async (
                IMediator mediator,
                [FromRoute] string id,
                [FromBody] RotateDeploymentTargetCredentialsRequestBody body,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new RotateDeploymentTargetCredentialsRequest(id, body),
                    cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("RotateDeploymentTargetCredentials")
            .Produces<DeploymentTargetDto>();
        return app;
    }
}
