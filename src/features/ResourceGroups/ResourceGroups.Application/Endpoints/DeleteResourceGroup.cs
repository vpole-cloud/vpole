using FluentValidation;
using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class DeleteResourceGroup
{
    public static IEndpointRouteBuilder MapDeleteResourceGroupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async ( IMediator mediator, [AsParameters] DeleteResourceGroupRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(response =>
                response.Deleted
                    ? Results.NoContent()
                    : Results.Accepted(value: response));
        })
        .WithName("DeleteResourceGroup")
        .WithSummary("Deletes a resource group by its ID.")
        .WithDescription(
            "Deletes an empty resource group immediately, or starts deletion of its runtime cells.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces<DeleteResourceGroupResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}

public sealed record DeleteResourceGroupRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null
) : IMediatorRequest<DeleteResourceGroupResponse>, ITenantWriteOperation;

public sealed class DeleteResourceGroupRequestValidator
    : AbstractValidator<DeleteResourceGroupRequest>
{
    public DeleteResourceGroupRequestValidator() =>
        RuleFor(request => request.Id)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Resource group ID must be a MongoDB ObjectId.");
}

public sealed record DeleteResourceGroupResponse(
    bool Deleted,
    bool DeletionRequested);

public sealed class DeleteResourceGroupHandler(
    IResourceGroupDbContext dbContext,
    IResourceDeploymentReferenceLookup deploymentReferences,
    TimeProvider timeProvider)
    : IMediatorHandler<DeleteResourceGroupRequest, DeleteResourceGroupResponse>
{
    public Task<Result<DeleteResourceGroupResponse>> HandleAsync(
        DeleteResourceGroupRequest request,
        CancellationToken cancellationToken = default) =>
        dbContext.RunInTransactionAsync<DeleteResourceGroupResponse>(
            (session, ct) => DeleteAsync(request, session, ct),
            cancellationToken: cancellationToken);

    private async Task<Result<DeleteResourceGroupResponse>> DeleteAsync(
        DeleteResourceGroupRequest request,
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
            await deploymentReferences.EnsureNoUnfinishedForResourceGroupAsync(
                context.ResourceGroup.TenantId,
                context.ResourceGroup.Id,
                session,
                cancellationToken);
        if (deploymentGuard is not ISuccess)
            return (Failure)deploymentGuard;

        if (context.Cells.All(cell => cell.State is DeletedRuntimeCellState))
        {
            return await DeleteCompletedAggregateAsync(
                request,
                context,
                session,
                cancellationToken);
        }

        return await RequestAggregateDeletionAsync(
            request,
            context,
            session,
            cancellationToken);
    }

    private async Task<Result<ResourceGroupDeletionContext>> LoadDeletionContextAsync(
        DeleteResourceGroupRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(session, ResourceGroupFilter(request))
            .FirstOrDefaultAsync(cancellationToken);
        if (resourceGroup is null)
            return Failures.NotFound("Resource group not found.", request.Id);
        if (resourceGroup.Resources.Count > 0)
        {
            return Failures.Conflict(
                "The resource group must not contain registered resources when deletion starts.");
        }

        var scopes = await dbContext.IsolationScopes
            .Find(session, ScopeFilter(resourceGroup))
            .ToListAsync(cancellationToken);
        var cells = await dbContext.RuntimeCells
            .Find(session, CellFilter(resourceGroup))
            .ToListAsync(cancellationToken);
        return new ResourceGroupDeletionContext(resourceGroup, scopes, cells);
    }

    private async Task<DeleteResourceGroupResponse> DeleteCompletedAggregateAsync(
        DeleteResourceGroupRequest request,
        ResourceGroupDeletionContext context,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        await dbContext.RuntimeCells.DeleteManyAsync(
            session,
            CellFilter(context.ResourceGroup),
            cancellationToken: cancellationToken);
        await dbContext.IsolationScopes.DeleteManyAsync(
            session,
            ScopeFilter(context.ResourceGroup),
            cancellationToken: cancellationToken);
        await dbContext.ResourceGroups.DeleteOneAsync(
            session,
            ResourceGroupFilter(request),
            cancellationToken: cancellationToken);
        await dbContext.StoreEventAsync(
            context.ResourceGroup.ToDeleteEvent(),
            session,
            cancellationToken);

        return new DeleteResourceGroupResponse(
            Deleted: true,
            DeletionRequested: true);
    }

    private async Task<Result<DeleteResourceGroupResponse>> RequestAggregateDeletionAsync(
        DeleteResourceGroupRequest request,
        ResourceGroupDeletionContext context,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var requestedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var parentFence = await RequestResourceGroupDeletionAsync(
            request,
            context.ResourceGroup,
            requestedAtUtc,
            session,
            cancellationToken);
        if (parentFence is not ISuccess)
            return (Failure)parentFence;

        var scopeBatch = CreateScopeDeletionBatch(context.Scopes, requestedAtUtc);
        if (scopeBatch.Writes.Count > 0)
        {
            await dbContext.IsolationScopes.BulkWriteAsync(
                session,
                scopeBatch.Writes,
                new BulkWriteOptions { IsOrdered = true },
                cancellationToken);
        }

        var cellBatch = CreateCellDeletionBatch(context.Cells, requestedAtUtc);
        if (cellBatch.Writes.Count > 0)
        {
            await dbContext.RuntimeCells.BulkWriteAsync(
                session,
                cellBatch.Writes,
                new BulkWriteOptions { IsOrdered = true },
                cancellationToken);
        }
        await dbContext.StoreEventAsync(
            scopeBatch.OutboxWrites.Concat(cellBatch.OutboxWrites),
            session,
            cancellationToken);

        return new DeleteResourceGroupResponse(
            Deleted: false,
            DeletionRequested: true);
    }

    private async Task<Result> RequestResourceGroupDeletionAsync(
        DeleteResourceGroupRequest request,
        ResourceGroup resourceGroup,
        DateTime requestedAtUtc,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (resourceGroup.Lifecycle is DeletingResourceGroupLifecycleState)
            return Result.Success();

        var deletingGroup = resourceGroup.RequestDeletion(requestedAtUtc);
        var replacement = await dbContext.ResourceGroups.ReplaceOneAsync(
            session,
            Builders<ResourceGroup>.Filter.And(
                ResourceGroupFilter(request),
                Builders<ResourceGroup>.Filter.Eq(
                    group => group.Revision,
                    resourceGroup.Revision),
                Builders<ResourceGroup>.Filter
                    .OfType<ResourceGroupLifecycleState, ActiveResourceGroupLifecycleState>(
                        group => group.Lifecycle)),
            deletingGroup,
            cancellationToken: cancellationToken);

        return replacement.ModifiedCount == 1
            ? Result.Success()
            : Failures.Conflict(
                "The resource group changed while deletion was starting.");
    }

    private static DeletionBatch<IsolationScope> CreateScopeDeletionBatch(
        IReadOnlyList<IsolationScope> scopes,
        DateTime requestedAtUtc)
    {
        var writes = new List<WriteModel<IsolationScope>>();
        var outboxWrites = new List<OutboxWrite>();
        foreach (var scope in scopes.Where(scope =>
                     scope.Lifecycle is not DeletingIsolationScopeLifecycleState))
        {
            var deletingScope = scope.RequestResourceGroupDeletion(requestedAtUtc);
            writes.Add(new ReplaceOneModel<IsolationScope>(
                Builders<IsolationScope>.Filter.And(
                    Builders<IsolationScope>.Filter.Eq(item => item.Id, scope.Id),
                    Builders<IsolationScope>.Filter.Eq(
                        item => item.Revision,
                        scope.Revision)),
                deletingScope));
            outboxWrites.Add(
                OutboxWrite.Create(deletingScope.ToDeletionRequestedEvent()));
        }

        return new DeletionBatch<IsolationScope>(writes, outboxWrites);
    }

    private static DeletionBatch<RuntimeCell> CreateCellDeletionBatch(
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

        return new DeletionBatch<RuntimeCell>(writes, outboxWrites);
    }

    private static FilterDefinition<ResourceGroup> ResourceGroupFilter(
        DeleteResourceGroupRequest request) =>
        Builders<ResourceGroup>.Filter.And(
            Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
            Builders<ResourceGroup>.Filter.Eq(group => group.Id, request.Id));

    private static FilterDefinition<IsolationScope> ScopeFilter(
        ResourceGroup resourceGroup) =>
        Builders<IsolationScope>.Filter.And(
            Builders<IsolationScope>.Filter.Eq(
                scope => scope.TenantId,
                resourceGroup.TenantId),
            Builders<IsolationScope>.Filter.Eq(
                scope => scope.ResourceGroupId,
                resourceGroup.Id));

    private static FilterDefinition<RuntimeCell> CellFilter(
        ResourceGroup resourceGroup) =>
        Builders<RuntimeCell>.Filter.And(
            Builders<RuntimeCell>.Filter.Eq(
                cell => cell.TenantId,
                resourceGroup.TenantId),
            Builders<RuntimeCell>.Filter.Eq(
                cell => cell.ResourceGroupId,
                resourceGroup.Id));

    private sealed record ResourceGroupDeletionContext(
        ResourceGroup ResourceGroup,
        IReadOnlyList<IsolationScope> Scopes,
        IReadOnlyList<RuntimeCell> Cells);

    private sealed record DeletionBatch<TEntity>(
        IReadOnlyList<WriteModel<TEntity>> Writes,
        IReadOnlyList<OutboxWrite> OutboxWrites);
}

public class DeleteKubernetesNamespaceOnResourceGroupDeletedConsumer(
    IKubernetesClientProvider kubernetesClientProvider
    ) : IConsumer<ResourceGroupDeletedEvent>
{
    public async Task Consume(ConsumeContext<ResourceGroupDeletedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;
        var kubernetes = await kubernetesClientProvider.GetDefaultAsync(ct);

        await kubernetes.CoreV1.DeleteNamespaceAsync(
            name: message.KubernetesNamespace,
            cancellationToken: ct);
    }
}
