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
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
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
    TimeProvider timeProvider)
    : IMediatorHandler<DeleteResourceGroupRequest, DeleteResourceGroupResponse>
{
    public async Task<Result<DeleteResourceGroupResponse>> HandleAsync(
        DeleteResourceGroupRequest request, CancellationToken cancellationToken = default)
    {
        var filter = Builders<ResourceGroup>.Filter.And(
            Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
            Builders<ResourceGroup>.Filter.Eq(rg => rg.Id, request.Id));

        return await dbContext.RunInTransactionAsync<DeleteResourceGroupResponse>(async (session, ct) =>
        {
            var current = await dbContext.ResourceGroups
                .Find(session, filter)
                .FirstOrDefaultAsync(ct);
            if (current is null)
                return Failures.NotFound("Resource group not found.", request.Id);
            if (current.Resources.Count > 0)
            {
                return Failures.Conflict(
                    "The resource group must not contain registered resources when deletion starts.");
            }

            var scopes = await dbContext.IsolationScopes
                .Find(session, scope =>
                    scope.TenantId == current.TenantId &&
                    scope.ResourceGroupId == current.Id)
                .ToListAsync(ct);
            var cells = await dbContext.RuntimeCells
                .Find(session, cell =>
                    cell.TenantId == current.TenantId &&
                    cell.ResourceGroupId == current.Id)
                .ToListAsync(ct);

            if (cells.All(cell => cell.State is DeletedRuntimeCellState))
            {
                await dbContext.RuntimeCells.DeleteManyAsync(
                    session,
                    cell =>
                        cell.TenantId == current.TenantId &&
                        cell.ResourceGroupId == current.Id,
                    cancellationToken: ct);
                await dbContext.IsolationScopes.DeleteManyAsync(
                    session,
                    scope =>
                        scope.TenantId == current.TenantId &&
                        scope.ResourceGroupId == current.Id,
                    cancellationToken: ct);
                await dbContext.ResourceGroups.DeleteOneAsync(
                    session,
                    filter,
                    cancellationToken: ct);

                await dbContext.StoreEventAsync(current.ToDeleteEvent(), session, ct);
                return new DeleteResourceGroupResponse(
                    Deleted: true,
                    DeletionRequested: true);
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var deletingGroup = current.RequestDeletion(now);
            if (current.Lifecycle is not DeletingResourceGroupLifecycleState)
            {
                var replacement = await dbContext.ResourceGroups.ReplaceOneAsync(
                    session,
                    Builders<ResourceGroup>.Filter.And(
                        filter,
                        Builders<ResourceGroup>.Filter.Eq(
                            group => group.Revision,
                            current.Revision),
                        Builders<ResourceGroup>.Filter.Eq("Lifecycle._t", "active")),
                    deletingGroup,
                    cancellationToken: ct);
                if (replacement.ModifiedCount != 1)
                    return Failures.Conflict("The resource group changed while deletion was starting.");
            }

            var deletionEvents = new List<IPoleEvent>();
            var scopeReplacements = new List<WriteModel<IsolationScope>>();
            foreach (var scope in scopes)
            {
                if (scope.Lifecycle is DeletingIsolationScopeLifecycleState)
                    continue;

                var deletingScope = scope.RequestResourceGroupDeletion(now);
                scopeReplacements.Add(new ReplaceOneModel<IsolationScope>(
                    Builders<IsolationScope>.Filter.And(
                        Builders<IsolationScope>.Filter.Eq(item => item.Id, scope.Id),
                        Builders<IsolationScope>.Filter.Eq(item => item.Revision, scope.Revision)),
                    deletingScope));
                deletionEvents.Add(deletingScope.ToDeletionRequestedEvent());
            }
            if (scopeReplacements.Count > 0)
            {
                await dbContext.IsolationScopes.BulkWriteAsync(
                    session,
                    scopeReplacements,
                    new BulkWriteOptions { IsOrdered = true },
                    ct);
            }

            var cellReplacements = new List<WriteModel<RuntimeCell>>();
            foreach (var cell in cells.Where(cell => cell.State is not DeletedRuntimeCellState))
            {
                if (cell.State is DeletingRuntimeCellState)
                    continue;

                var deletingCell = cell.RequestDeletion(now);
                cellReplacements.Add(new ReplaceOneModel<RuntimeCell>(
                    Builders<RuntimeCell>.Filter.And(
                        Builders<RuntimeCell>.Filter.Eq(item => item.Id, cell.Id),
                        Builders<RuntimeCell>.Filter.Eq(
                            item => item.DesiredGeneration,
                            cell.DesiredGeneration)),
                    deletingCell));
                deletionEvents.Add(deletingCell.ToDeletionRequestedEvent());
            }
            if (cellReplacements.Count > 0)
            {
                await dbContext.RuntimeCells.BulkWriteAsync(
                    session,
                    cellReplacements,
                    new BulkWriteOptions { IsOrdered = true },
                    ct);
            }
            await dbContext.StoreEventAsync(deletionEvents, session, ct);

            return new DeleteResourceGroupResponse(
                Deleted: false,
                DeletionRequested: true);
        }, cancellationToken: cancellationToken);
    }
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
