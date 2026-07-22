using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record UpdateDeploymentTargetRequestBody(
    string DisplayName,
    bool Enabled,
    bool AcceptsWorkloads,
    long Revision);

public sealed record UpdateDeploymentTargetRequest(string Id, UpdateDeploymentTargetRequestBody Body)
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class UpdateDeploymentTargetHandler(
    IDeploymentTargetDbContext dbContext,
    IDeploymentTargetClientCache clientCache,
    TimeProvider timeProvider)
    : IMediatorHandler<UpdateDeploymentTargetRequest, DeploymentTargetDto>
{
    public async Task<Result<DeploymentTargetDto>> HandleAsync(
        UpdateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var bodyValidation = ValidateBody(request.Body);
        if (bodyValidation is not null)
            return bodyValidation;
        var targetResult = await DeploymentTargetEndpointSupport.GetTargetAsync(
            dbContext,
            request.Id,
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;
        if (target.Revision != request.Body.Revision)
            return Failures.Conflict("The deployment target changed. Reload it and retry.");

        DeploymentTarget updated;
        try
        {
            updated = target.Reconfigure(
                request.Body.DisplayName,
                request.Body.Enabled,
                request.Body.AcceptsWorkloads,
                DeploymentTargetEndpointSupport.CheckedAtUtc(timeProvider, target.UpdatedAtUtc));
        }
        catch (ArgumentException)
        {
            return DeploymentTargetEndpointSupport.BodyValidationFailure(
                "The deployment-target update is invalid.");
        }
        catch (InvalidOperationException)
        {
            return DeploymentTargetEndpointSupport.BodyValidationFailure(
                "The deployment-target update is invalid.");
        }

        if (ReferenceEquals(updated, target))
        {
            var existingDefaultId = await DeploymentTargetEndpointSupport.GetDefaultIdAsync(
                dbContext,
                cancellationToken);
            return target.ToDto(target.Id == existingDefaultId);
        }

        var persisted = await dbContext.DeploymentTargets.FindOneAndUpdateAsync(
            DeploymentTargetEndpointSupport.RevisionFilter(target),
            Builders<DeploymentTarget>.Update.Combine(
                Builders<DeploymentTarget>.Update.Set(item => item.DisplayName, updated.DisplayName),
                Builders<DeploymentTarget>.Update.Set(item => item.Enabled, updated.Enabled),
                Builders<DeploymentTarget>.Update.Set(
                    item => item.AcceptsWorkloads,
                    updated.AcceptsWorkloads),
                Builders<DeploymentTarget>.Update.Set(item => item.Revision, updated.Revision),
                Builders<DeploymentTarget>.Update.Set(item => item.UpdatedAtUtc, updated.UpdatedAtUtc)),
            new FindOneAndUpdateOptions<DeploymentTarget>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        if (persisted is null)
            return Failures.Conflict("The deployment target changed. Reload it and retry.");

        if (!persisted.Enabled)
            clientCache.Invalidate(target.Id);
        var defaultId = await DeploymentTargetEndpointSupport.GetDefaultIdAsync(dbContext, cancellationToken);
        return persisted.ToDto(persisted.Id == defaultId);
    }

    private static ValidationFailure? ValidateBody(UpdateDeploymentTargetRequestBody? body)
    {
        if (body is null)
            return DeploymentTargetEndpointSupport.BodyValidationFailure("Request body is required.");
        if (string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Trim().Length > 80)
        {
            return DeploymentTargetEndpointSupport.BodyValidationFailure(
                "Display name is required and must be 80 characters or less.");
        }
        if (body.Revision < 1)
            return DeploymentTargetEndpointSupport.BodyValidationFailure("A positive revision is required.");
        if (!body.Enabled && body.AcceptsWorkloads)
        {
            return DeploymentTargetEndpointSupport.BodyValidationFailure(
                "A disabled deployment target cannot accept workloads.");
        }
        return null;
    }
}

public static class UpdateDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapUpdateDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{id}", async (
                IMediator mediator,
                [FromRoute] string id,
                [FromBody] UpdateDeploymentTargetRequestBody body,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new UpdateDeploymentTargetRequest(id, body),
                    cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateDeploymentTarget")
            .Produces<DeploymentTargetDto>();
        return app;
    }
}
