using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
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
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null)
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
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Resource group ID must be a MongoDB ObjectId.");
        RuleFor(request => request.ScopeId)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Isolation scope ID must be a MongoDB ObjectId.");
    }
}

public sealed class DeleteIsolationScopeHandler(
    IResourceGroupDbContext dbContext,
    TimeProvider timeProvider)
    : IMediatorHandler<DeleteIsolationScopeRequest, DeleteIsolationScopeResponse>
{
    public async Task<Result<DeleteIsolationScopeResponse>> HandleAsync(
        DeleteIsolationScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await dbContext.RunInTransactionAsync<DeleteIsolationScopeResponse>(
            async (session, ct) =>
            {
                var resourceGroupFilter = Builders<ResourceGroup>.Filter.And(
                    Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
                    Builders<ResourceGroup>.Filter.Eq(group => group.Id, request.ResourceGroupId));
                var resourceGroup = await dbContext.ResourceGroups
                    .Find(session, resourceGroupFilter)
                    .FirstOrDefaultAsync(ct);
                if (resourceGroup is null)
                    return Failures.NotFound("Resource group not found.", request.ResourceGroupId);
                if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState)
                    return Failures.Conflict("The resource group is being deleted.");

                var scopeFilter = Builders<IsolationScope>.Filter.And(
                    Builders<IsolationScope>.Filter.Eq(scope => scope.Id, request.ScopeId),
                    Builders<IsolationScope>.Filter.Eq(scope => scope.ResourceGroupId, resourceGroup.Id),
                    Builders<IsolationScope>.Filter.Eq(scope => scope.TenantId, resourceGroup.TenantId));
                var scope = await dbContext.IsolationScopes
                    .Find(session, scopeFilter)
                    .FirstOrDefaultAsync(ct);
                if (scope is null)
                    return Failures.NotFound("Isolation scope not found.", request.ScopeId);
                if (scope.IsDefault)
                    return Failures.Conflict("The default isolation scope cannot be deleted separately.");

                var cells = await dbContext.RuntimeCells
                    .Find(session, cell =>
                        cell.TenantId == resourceGroup.TenantId &&
                        cell.ResourceGroupId == resourceGroup.Id &&
                        cell.IsolationScopeId == scope.Id)
                    .ToListAsync(ct);
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var fence = await dbContext.ResourceGroups.UpdateOneAsync(
                    session,
                    Builders<ResourceGroup>.Filter.And(
                        resourceGroupFilter,
                        Builders<ResourceGroup>.Filter.Eq(
                            group => group.Revision,
                            resourceGroup.Revision),
                        Builders<ResourceGroup>.Filter.Eq("Lifecycle._t", "active")),
                    Builders<ResourceGroup>.Update
                        .Inc(group => group.Revision, 1)
                        .Set(group => group.UpdatedAtUtc, now),
                    cancellationToken: ct);
                if (fence.ModifiedCount != 1)
                {
                    return Failures.Conflict(
                        "The resource group changed while the isolation scope was being deleted.");
                }

                if (cells.All(cell => cell.State is DeletedRuntimeCellState))
                {
                    var deletionRequested = scope.RequestDeletion(now);
                    await dbContext.RuntimeCells.DeleteManyAsync(
                        session,
                        cell =>
                            cell.TenantId == resourceGroup.TenantId &&
                            cell.ResourceGroupId == resourceGroup.Id &&
                            cell.IsolationScopeId == scope.Id,
                        cancellationToken: ct);
                    await dbContext.IsolationScopes.DeleteOneAsync(
                        session,
                        scopeFilter,
                        cancellationToken: ct);
                    await dbContext.StoreEventAsync(
                        deletionRequested.ToDeletionRequestedEvent(),
                        session,
                        ct);
                    return new DeleteIsolationScopeResponse(scope.Id, true, true);
                }

                var deletingScope = scope.RequestDeletion(now);
                await dbContext.IsolationScopes.ReplaceOneAsync(
                    session,
                    Builders<IsolationScope>.Filter.And(
                        scopeFilter,
                        Builders<IsolationScope>.Filter.Eq(item => item.Revision, scope.Revision)),
                    deletingScope,
                    cancellationToken: ct);

                var deletionEvents = new List<IPoleEvent>();
                if (scope.Lifecycle is not DeletingIsolationScopeLifecycleState)
                    deletionEvents.Add(deletingScope.ToDeletionRequestedEvent());

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

                return new DeleteIsolationScopeResponse(scope.Id, false, true);
            },
            cancellationToken: cancellationToken);
        return result;
    }
}
