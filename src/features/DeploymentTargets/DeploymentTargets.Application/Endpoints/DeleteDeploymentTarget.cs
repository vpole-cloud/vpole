using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record DeleteDeploymentTargetRequest(
    [FromRoute] string Id)
    : IMediatorRequest<string>;

public sealed class DeleteDeploymentTargetHandler(
    IDeploymentTargetDbContext dbContext,
    DeploymentTargetReader reader,
    IDeploymentTargetDeletionGuard deletionGuard,
    IDeploymentTargetClientCache clientCache,
    TimeProvider timeProvider)
    : IMediatorHandler<DeleteDeploymentTargetRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await reader.GetAsync(request.Id, cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;
        var defaultId = await reader.GetDefaultIdAsync(cancellationToken);
        if (target.Id == defaultId)
            return Failures.Conflict("The default deployment target cannot be deleted.");

        var fencedTarget = target;
        if (target.DeletionRequestedAtUtc is null)
        {
            var deletionRequested = target.RequestDeletion(
                DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc));
            var persistedFence = await dbContext.DeploymentTargets.FindOneAndUpdateAsync(
                ReferenceFenceFilter(target),
                Builders<DeploymentTarget>.Update.Combine(
                    Builders<DeploymentTarget>.Update.Set(
                        item => item.AcceptsWorkloads,
                        deletionRequested.AcceptsWorkloads),
                    Builders<DeploymentTarget>.Update.Set(
                        item => item.DeletionRequestedAtUtc,
                        deletionRequested.DeletionRequestedAtUtc),
                    Builders<DeploymentTarget>.Update.Set(
                        item => item.Revision,
                        deletionRequested.Revision),
                    Builders<DeploymentTarget>.Update.Set(
                        item => item.UpdatedAtUtc,
                        deletionRequested.UpdatedAtUtc)),
                new FindOneAndUpdateOptions<DeploymentTarget>
                {
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken);
            if (persistedFence is null)
                return Failures.Conflict("The deployment target changed. Reload it and retry.");
            fencedTarget = persistedFence;
        }

        var canDelete = await deletionGuard.EnsureNoReferencesAsync(
            fencedTarget.Id,
            cancellationToken);
        if (canDelete is not ISuccess)
            return (Failure)canDelete;

        var deleted = await dbContext.RunInTransactionAsync<string>(async (session, ct) =>
        {
            var current = await dbContext.DeploymentTargets
                .Find(session, DeletionFenceFilter(fencedTarget))
                .FirstOrDefaultAsync(ct);
            if (current is null)
                return Failures.Conflict("The deployment target changed. Reload it and retry.");

            var currentDefaultId = await dbContext.DeploymentTargetSettings
                .Find(session, settings => settings.Id == DeploymentTargetSettings.SingletonId)
                .Project(settings => settings.DefaultDeploymentTargetId)
                .FirstOrDefaultAsync(ct);
            if (current.Id == currentDefaultId)
                return Failures.Conflict("The default deployment target cannot be deleted.");

            var deletion = await dbContext.DeploymentTargets.DeleteOneAsync(
                session,
                DeletionFenceFilter(current),
                cancellationToken: ct);
            return deletion.DeletedCount == 1
                ? current.Id
                : Failures.Conflict("The deployment target changed. Reload it and retry.");
        }, cancellationToken: cancellationToken);
        if (!deleted.TryGetValue(out var deletedId, out var deletionFailure))
            return deletionFailure;

        clientCache.Invalidate(deletedId);
        return deletedId;
    }

    private static FilterDefinition<DeploymentTarget> ReferenceFenceFilter(DeploymentTarget target) =>
        Builders<DeploymentTarget>.Filter.And(
            DeploymentTargetFilters.RevisionFilter(target),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.ReferenceFence,
                target.ReferenceFence),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.DeletionRequestedAtUtc,
                null));

    private static FilterDefinition<DeploymentTarget> DeletionFenceFilter(DeploymentTarget target) =>
        Builders<DeploymentTarget>.Filter.And(
            DeploymentTargetFilters.RevisionFilter(target),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.ReferenceFence,
                target.ReferenceFence),
            Builders<DeploymentTarget>.Filter.Eq(
                item => item.DeletionRequestedAtUtc,
                target.DeletionRequestedAtUtc));
}

public sealed class DeleteDeploymentTargetRequestValidator
    : AbstractValidator<DeleteDeploymentTargetRequest>
{
    public DeleteDeploymentTargetRequestValidator() =>
        RuleFor(request => request.Id)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Deployment target ID must be a MongoDB ObjectId.");
}

public static class DeleteDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapDeleteDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                [AsParameters] DeleteDeploymentTargetRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteDeploymentTarget")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }
}
