using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
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
    [FromRoute] string Id,
    [FromBody] RotateDeploymentTargetCredentialsRequestBody Body)
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class RotateDeploymentTargetCredentialsHandler(
    IDeploymentTargetDbContext dbContext,
    DeploymentTargetReader reader,
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
        var targetResult = await reader.GetAsync(request.Id, cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;
        if (target.Revision != request.Body.Revision)
            return Failures.Conflict("The deployment target changed. Reload it and retry.");

        var preparedResult = await connectionPreparer.PrepareAsync(
            target.Id,
            target.NextConnectionVersion,
            request.Body.Connection.ToInput(),
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
                return ConnectionValidationFailure(
                    observation.Health.FailureMessage ?? "The Kubernetes connection is not usable.");
            }
            if (!string.Equals(target.ClusterIdentity, observation.ClusterIdentity, StringComparison.Ordinal))
            {
                return ConnectionValidationFailure(
                    "Replacement credentials resolve to a different Kubernetes cluster.");
            }

            var updated = target.ReplaceConnectionCredential(
                    observation.ClusterIdentity,
                    prepared.Connection,
                    DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc))
                .ApplyValidationObservation(
                    target.Revision + 1,
                    prepared.Connection.Version,
                    observation.Health,
                    observation.Capabilities);
            var persisted = await dbContext.DeploymentTargets.FindOneAndUpdateAsync(
                Builders<DeploymentTarget>.Filter.And(
                    DeploymentTargetFilters.RevisionFilter(target),
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
            var defaultId = await reader.GetDefaultIdAsync(cancellationToken);
            return persisted.ToDto(persisted.Id == defaultId);
        }
    }

    private static ValidationFailure ConnectionValidationFailure(string safeMessage) =>
        Failures.ValidationFailure(new Dictionary<string, string[]>
        {
            ["Body.Connection"] = [safeMessage]
        });
}

public sealed class RotateDeploymentTargetCredentialsRequestValidator
    : AbstractValidator<RotateDeploymentTargetCredentialsRequest>
{
    public RotateDeploymentTargetCredentialsRequestValidator()
    {
        RuleFor(request => request.Id)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Deployment target ID must be a MongoDB ObjectId.");
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
                [AsParameters] RotateDeploymentTargetCredentialsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("RotateDeploymentTargetCredentials")
            .Produces<DeploymentTargetDto>();
        return app;
    }
}
