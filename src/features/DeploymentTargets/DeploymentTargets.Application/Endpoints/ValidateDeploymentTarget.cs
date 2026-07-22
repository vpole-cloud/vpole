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

public sealed record ValidateDeploymentTargetRequest(string Id)
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class ValidateDeploymentTargetHandler(
    IDeploymentTargetDbContext dbContext,
    IDeploymentTargetKubernetesClientFactory clientFactory,
    IDeploymentTargetProbe probe,
    TimeProvider timeProvider)
    : IMediatorHandler<ValidateDeploymentTargetRequest, DeploymentTargetDto>
{
    public async Task<Result<DeploymentTargetDto>> HandleAsync(
        ValidateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await DeploymentTargetEndpointSupport.GetTargetAsync(
            dbContext,
            request.Id,
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;

        DeploymentTargetProbeObservation observation;
        var clientResult = await clientFactory.CreateAsync(target.Id, target.Connection, cancellationToken);
        if (!clientResult.TryGetValue(out var client, out _))
        {
            var checkedAt = DeploymentTargetEndpointSupport.CheckedAtUtc(
                timeProvider,
                target.Health.LastHealthyAtUtc);
            observation = new DeploymentTargetProbeObservation(
                null,
                DeploymentTargetHealth.Degraded(
                    target.Connection.Version,
                    checkedAt,
                    target.Health.LastHealthyAtUtc,
                    "StoredConnectionUnavailable",
                    "The stored Kubernetes connection could not be loaded."),
                DeploymentTargetCapabilities.Unknown(target.Connection.Version));
        }
        else
        {
            using (client)
            {
                observation = await probe.ProbeAsync(
                    client,
                    target.Connection.Version,
                    target.Health.LastHealthyAtUtc,
                    cancellationToken);
            }
        }

        if (observation.ClusterIdentity is not null &&
            !string.Equals(target.ClusterIdentity, observation.ClusterIdentity, StringComparison.Ordinal))
        {
            observation = observation with
            {
                Health = DeploymentTargetHealth.Degraded(
                    target.Connection.Version,
                    DeploymentTargetEndpointSupport.CheckedAtUtc(
                        timeProvider,
                        target.Health.LastHealthyAtUtc),
                    target.Health.LastHealthyAtUtc,
                    "ClusterIdentityMismatch",
                    "The stored connection resolved to a different Kubernetes cluster."),
                Capabilities = DeploymentTargetCapabilities.Unknown(target.Connection.Version)
            };
        }

        var updated = target.ApplyValidationObservation(
            target.Revision,
            target.Connection.Version,
            observation.Health,
            observation.Capabilities);
        var persisted = await dbContext.DeploymentTargets.FindOneAndUpdateAsync(
            Builders<DeploymentTarget>.Filter.And(
                DeploymentTargetEndpointSupport.RevisionFilter(target),
                Builders<DeploymentTarget>.Filter.Eq(
                    item => item.Connection.Version,
                    target.Connection.Version)),
            Builders<DeploymentTarget>.Update.Combine(
                Builders<DeploymentTarget>.Update.Set(item => item.Health, updated.Health),
                Builders<DeploymentTarget>.Update.Set(item => item.Capabilities, updated.Capabilities)),
            new FindOneAndUpdateOptions<DeploymentTarget>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        if (persisted is null)
            return Failures.Conflict("The deployment target changed while it was being validated.");

        var defaultId = await DeploymentTargetEndpointSupport.GetDefaultIdAsync(dbContext, cancellationToken);
        return persisted.ToDto(persisted.Id == defaultId);
    }
}

public static class ValidateDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapValidateDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{id}/validate", async (
                IMediator mediator,
                [FromRoute] string id,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new ValidateDeploymentTargetRequest(id), cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("ValidateDeploymentTarget")
            .Produces<DeploymentTargetDto>();
        return app;
    }
}
