using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class DeleteIsolationScope
{
    public static IEndpointRouteBuilder MapDeleteIsolationScopeEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapDelete(
                "/{ResourceGroupId}/isolation-scopes/{ScopeId}",
                async (
                    IMediator mediator,
                    [AsParameters] DeleteIsolationScopeRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(response =>
                        response.Deleted
                            ? Results.NoContent()
                            : Results.Accepted(value: response));
                })
            .WithName("DeleteIsolationScope")
            .WithSummary("Deletes an additional isolation scope.")
            .WithDescription(
                "Deletes an empty additional scope immediately, or starts deletion of its runtime cells.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<DeleteIsolationScopeResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}

public sealed record DeleteIsolationScopeRequest(
    [FromRoute] string ResourceGroupId,
    [FromRoute] string ScopeId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<DeleteIsolationScopeResponse>, ITenantWriteOperation;

public sealed record DeleteIsolationScopeResponse(
    string Id,
    bool Deleted,
    bool DeletionRequested);

public sealed class DeleteIsolationScopeRequestValidator
    : AbstractValidator<DeleteIsolationScopeRequest>
{
    public DeleteIsolationScopeRequestValidator()
    {
        RuleFor(request => request.ResourceGroupId)
            .MongoObjectId("Resource group ID");
        RuleFor(request => request.ScopeId)
            .MongoObjectId("Isolation scope ID");
    }
}

public sealed class DeleteIsolationScopeHandler(
    IResourceGroupDbContext dbContext,
    IResourceDeploymentReferenceLookup deploymentReferences,
    TimeProvider timeProvider)
    : IMediatorHandler<DeleteIsolationScopeRequest, DeleteIsolationScopeResponse>
{
    public Task<Result<DeleteIsolationScopeResponse>> HandleAsync(
        DeleteIsolationScopeRequest request,
        CancellationToken cancellationToken = default) =>
        dbContext.RunInTransactionAsync<DeleteIsolationScopeResponse>(
            (session, ct) => DeleteAsync(request, session, ct),
            cancellationToken: cancellationToken);

    private async Task<Result<DeleteIsolationScopeResponse>> DeleteAsync(
        DeleteIsolationScopeRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var contextResult = await LoadDeletionContextAsync(
            request,
            session,
            cancellationToken);
        if (!contextResult.TryGetValue(out var context, out var loadFailure))
            return loadFailure;

        var deploymentGuard =
            await deploymentReferences.EnsureNoUnfinishedForIsolationScopeAsync(
                context.ResourceGroup.TenantId,
                context.ResourceGroup.Id,
                context.Scope.Id,
                session,
                cancellationToken);
        if (deploymentGuard is not ISuccess)
            return (Failure)deploymentGuard;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var fence = await FenceResourceGroupAsync(
            context.ResourceGroup,
            request,
            now,
            session,
            cancellationToken);
        if (fence is not ISuccess)
            return (Failure)fence;

        return context.Cells.All(cell => cell.State is DeletedRuntimeCellState)
            ? await DeleteCompletedScopeAsync(context, now, session, cancellationToken)
            : await RequestScopeDeletionAsync(context, now, session, cancellationToken);
    }

    private async Task<Result<IsolationScopeDeletionContext>> LoadDeletionContextAsync(
        DeleteIsolationScopeRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(session, ResourceGroupFilter(request))
            .FirstOrDefaultAsync(cancellationToken);
        if (resourceGroup is null)
            return Failures.NotFound("Resource group not found.", request.ResourceGroupId);
        if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState)
            return Failures.Conflict("The resource group is being deleted.");

        var scope = await dbContext.IsolationScopes
            .Find(session, ScopeFilter(request.ScopeId, resourceGroup))
            .FirstOrDefaultAsync(cancellationToken);
        if (scope is null)
            return Failures.NotFound("Isolation scope not found.", request.ScopeId);
        if (scope.IsDefault)
            return Failures.Conflict("The default isolation scope cannot be deleted separately.");

        var cells = await dbContext.RuntimeCells
            .Find(session, CellFilter(resourceGroup, scope.Id))
            .ToListAsync(cancellationToken);
        return new IsolationScopeDeletionContext(resourceGroup, scope, cells);
    }

    private async Task<Result> FenceResourceGroupAsync(
        ResourceGroup resourceGroup,
        DeleteIsolationScopeRequest request,
        DateTime fencedAtUtc,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var fence = await dbContext.ResourceGroups.UpdateOneAsync(
            session,
            Builders<ResourceGroup>.Filter.And(
                ResourceGroupFilter(request),
                Builders<ResourceGroup>.Filter.Eq(
                    group => group.Revision,
                    resourceGroup.Revision),
                Builders<ResourceGroup>.Filter
                    .OfType<ResourceGroupLifecycleState, ActiveResourceGroupLifecycleState>(
                        group => group.Lifecycle)),
            Builders<ResourceGroup>.Update
                .Inc(group => group.Revision, 1)
                .Set(group => group.UpdatedAtUtc, fencedAtUtc),
            cancellationToken: cancellationToken);

        return fence.ModifiedCount == 1
            ? Result.Success()
            : Failures.Conflict(
                "The resource group changed while the isolation scope was being deleted.");
    }

    private async Task<DeleteIsolationScopeResponse> DeleteCompletedScopeAsync(
        IsolationScopeDeletionContext context,
        DateTime requestedAtUtc,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var deletionRequested = context.Scope.RequestDeletion(requestedAtUtc);
        await dbContext.RuntimeCells.DeleteManyAsync(
            session,
            CellFilter(context.ResourceGroup, context.Scope.Id),
            cancellationToken: cancellationToken);
        await dbContext.IsolationScopes.DeleteOneAsync(
            session,
            ScopeFilter(context.Scope.Id, context.ResourceGroup),
            cancellationToken: cancellationToken);
        await dbContext.StoreEventAsync(
            deletionRequested.ToDeletionRequestedEvent(),
            session,
            cancellationToken);
        return new DeleteIsolationScopeResponse(context.Scope.Id, true, true);
    }

    private async Task<DeleteIsolationScopeResponse> RequestScopeDeletionAsync(
        IsolationScopeDeletionContext context,
        DateTime requestedAtUtc,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var deletingScope = context.Scope.RequestDeletion(requestedAtUtc);
        await dbContext.IsolationScopes.ReplaceOneAsync(
            session,
            Builders<IsolationScope>.Filter.And(
                ScopeFilter(context.Scope.Id, context.ResourceGroup),
                Builders<IsolationScope>.Filter.Eq(
                    scope => scope.Revision,
                    context.Scope.Revision)),
            deletingScope,
            cancellationToken: cancellationToken);

        var outboxWrites = new List<OutboxWrite>();
        if (context.Scope.Lifecycle is not DeletingIsolationScopeLifecycleState)
        {
            outboxWrites.Add(
                OutboxWrite.Create(deletingScope.ToDeletionRequestedEvent()));
        }

        var cellBatch = CreateCellDeletionBatch(
            context.Cells,
            requestedAtUtc);
        if (cellBatch.Writes.Count > 0)
        {
            await dbContext.RuntimeCells.BulkWriteAsync(
                session,
                cellBatch.Writes,
                new BulkWriteOptions { IsOrdered = true },
                cancellationToken);
        }
        await dbContext.StoreEventAsync(
            outboxWrites.Concat(cellBatch.OutboxWrites),
            session,
            cancellationToken);

        return new DeleteIsolationScopeResponse(context.Scope.Id, false, true);
    }

    private static CellDeletionBatch CreateCellDeletionBatch(
        IReadOnlyList<RuntimeCell> cells,
        DateTime requestedAtUtc)
    {
        var writes = new List<WriteModel<RuntimeCell>>();
        var outboxWrites = new List<OutboxWrite>();
        foreach (var cell in cells.Where(cell =>
                     cell.State is not (DeletedRuntimeCellState or DeletingRuntimeCellState)))
        {
            var deletingCell = cell.RequestDeletion(requestedAtUtc);
            writes.Add(new ReplaceOneModel<RuntimeCell>(
                Builders<RuntimeCell>.Filter.And(
                    Builders<RuntimeCell>.Filter.Eq(item => item.Id, cell.Id),
                    Builders<RuntimeCell>.Filter.Eq(
                        item => item.DesiredGeneration,
                        cell.DesiredGeneration)),
                deletingCell));
            outboxWrites.Add(OutboxWrite.Create(
                deletingCell.ToDeletionRequestedEvent(),
                RuntimeCellPendingKey.Deletion(
                    deletingCell.Id,
                    deletingCell.DesiredGeneration)));
        }

        return new CellDeletionBatch(writes, outboxWrites);
    }

    private static FilterDefinition<ResourceGroup> ResourceGroupFilter(
        DeleteIsolationScopeRequest request) =>
        Builders<ResourceGroup>.Filter.And(
            Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
            Builders<ResourceGroup>.Filter.Eq(group => group.Id, request.ResourceGroupId));

    private static FilterDefinition<IsolationScope> ScopeFilter(
        string scopeId,
        ResourceGroup resourceGroup) =>
        Builders<IsolationScope>.Filter.And(
            Builders<IsolationScope>.Filter.Eq(scope => scope.Id, scopeId),
            Builders<IsolationScope>.Filter.Eq(
                scope => scope.ResourceGroupId,
                resourceGroup.Id),
            Builders<IsolationScope>.Filter.Eq(
                scope => scope.TenantId,
                resourceGroup.TenantId));

    private static FilterDefinition<RuntimeCell> CellFilter(
        ResourceGroup resourceGroup,
        string scopeId) =>
        Builders<RuntimeCell>.Filter.And(
            Builders<RuntimeCell>.Filter.Eq(
                cell => cell.TenantId,
                resourceGroup.TenantId),
            Builders<RuntimeCell>.Filter.Eq(
                cell => cell.ResourceGroupId,
                resourceGroup.Id),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.IsolationScopeId, scopeId));

    private sealed record IsolationScopeDeletionContext(
        ResourceGroup ResourceGroup,
        IsolationScope Scope,
        IReadOnlyList<RuntimeCell> Cells);

    private sealed record CellDeletionBatch(
        IReadOnlyList<WriteModel<RuntimeCell>> Writes,
        IReadOnlyList<OutboxWrite> OutboxWrites);
}
