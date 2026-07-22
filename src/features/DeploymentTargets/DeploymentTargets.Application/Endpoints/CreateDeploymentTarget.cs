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

public sealed record CreateDeploymentTargetRequestBody(
    string DisplayName,
    bool Enabled,
    bool AcceptsWorkloads,
    DeploymentTargetConnectionRequestBody Connection);

public sealed record CreateDeploymentTargetRequest(CreateDeploymentTargetRequestBody Body)
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class CreateDeploymentTargetHandler(
    IDeploymentTargetDbContext dbContext,
    IDeploymentTargetConnectionPreparer connectionPreparer,
    IDeploymentTargetProbe probe,
    TimeProvider timeProvider)
    : IMediatorHandler<CreateDeploymentTargetRequest, DeploymentTargetDto>
{
    public async Task<Result<DeploymentTargetDto>> HandleAsync(
        CreateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var bodyValidation = ValidateBody(request.Body);
        if (bodyValidation is not null)
            return bodyValidation;
        var inputResult = DeploymentTargetEndpointSupport.ToConnectionInput(request.Body.Connection);
        if (!inputResult.TryGetValue(out var input, out var inputFailure))
            return inputFailure;

        var id = ObjectId.GenerateNewId().ToString();
        var preparedResult = await connectionPreparer.PrepareAsync(id, 1, input, cancellationToken);
        if (!preparedResult.TryGetValue(out var prepared, out var prepareFailure))
            return prepareFailure;

        using (prepared)
        {
            var observation = await probe.ProbeAsync(
                prepared.Candidate,
                prepared.Connection.Version,
                cancellationToken: cancellationToken);
            if (!observation.IsUsable || observation.ClusterIdentity is null)
            {
                return DeploymentTargetEndpointSupport.ConnectionValidationFailure(
                    observation.Health.FailureMessage ?? "The Kubernetes connection is not usable.");
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var target = DeploymentTargetFactory.Create(
                request.Body.DisplayName,
                observation.ClusterIdentity,
                prepared.Connection,
                observation.Health,
                observation.Capabilities,
                request.Body.Enabled,
                request.Body.AcceptsWorkloads,
                new DeploymentTargetFactory.CreateOptions(id, now));
            try
            {
                await dbContext.DeploymentTargets.InsertOneAsync(target, cancellationToken: cancellationToken);
            }
            catch (MongoWriteException exception) when (
                exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                return Failures.Conflict("A deployment target for this Kubernetes cluster already exists.");
            }

            return target.ToDto();
        }
    }

    private static ValidationFailure? ValidateBody(CreateDeploymentTargetRequestBody? body)
    {
        if (body is null)
            return DeploymentTargetEndpointSupport.BodyValidationFailure("Request body is required.");
        if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Trim().Length > 80)
        {
            return DeploymentTargetEndpointSupport.BodyValidationFailure(
                "Display name is required and must be 80 characters or less.");
        }
        if (!body.Enabled && body.AcceptsWorkloads)
        {
            return DeploymentTargetEndpointSupport.BodyValidationFailure(
                "A disabled deployment target cannot accept workloads.");
        }
        if (body.Connection is null)
            return DeploymentTargetEndpointSupport.BodyValidationFailure("Connection is required.");
        return null;
    }
}

public static class CreateDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapCreateDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (
                IMediator mediator,
                [FromBody] CreateDeploymentTargetRequestBody body,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new CreateDeploymentTargetRequest(body), cancellationToken);
                return result.ToHttpResult(target =>
                    Results.Created($"/api/v1/platform/deployment-targets/{target.Id}", target));
            })
            .WithName("CreateDeploymentTarget")
            .Produces<DeploymentTargetDto>(StatusCodes.Status201Created);
        return app;
    }
}
