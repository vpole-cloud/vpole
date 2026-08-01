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
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record RetryRuntimeCellRequest(
    [FromRoute] string ResourceGroupId,
    [FromRoute] string CellId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<RuntimeCellDto>, ITenantWriteOperation;

public sealed class RetryRuntimeCellHandler(
    IResourceGroupDbContext dbContext,
    TimeProvider timeProvider)
    : IMediatorHandler<RetryRuntimeCellRequest, RuntimeCellDto>
{
    public async Task<Result<RuntimeCellDto>> HandleAsync(
        RetryRuntimeCellRequest request,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.RunInTransactionAsync<RuntimeCellDto>(async (session, ct) =>
        {
            var filter = Builders<RuntimeCell>.Filter.And(
                Builders<RuntimeCell>.Filter.Eq(cell => cell.Id, request.CellId),
                Builders<RuntimeCell>.Filter.Eq(cell => cell.ResourceGroupId, request.ResourceGroupId),
                Builders<RuntimeCell>.Filter.ForTenant(request.TenantId));
            var current = await dbContext.RuntimeCells
                .Find(session, filter)
                .FirstOrDefaultAsync(ct);
            if (current is null)
                return Failures.NotFound("Runtime cell not found.", request.CellId);
            if (current.State is not FailedRuntimeCellState failed)
                return Failures.Conflict("Only a failed runtime cell can be retried explicitly.");

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var retried = failed.Operation switch
            {
                RuntimeCellOperation.Reconciliation => current.RequestReconciliation(now),
                RuntimeCellOperation.Deletion => current.RequestDeletion(now),
                _ => throw new InvalidOperationException(
                    $"Unsupported runtime cell operation '{failed.Operation}'.")
            };
            var replacement = await dbContext.RuntimeCells.ReplaceOneAsync(
                session,
                Builders<RuntimeCell>.Filter.And(
                    filter,
                    Builders<RuntimeCell>.Filter.Eq(
                        cell => cell.DesiredGeneration,
                        current.DesiredGeneration),
                    Builders<RuntimeCell>.Filter
                        .OfType<RuntimeCellState, FailedRuntimeCellState>(
                            cell => cell.State)),
                retried,
                cancellationToken: ct);
            if (replacement.ModifiedCount != 1)
                return Failures.Conflict("The runtime cell changed while retry was being requested.");

            if (failed.Operation == RuntimeCellOperation.Reconciliation)
            {
                await dbContext.StoreEventAsync(
                    OutboxWrite.Create(
                        retried.ToReconciliationRequestedEvent(),
                        RuntimeCellPendingKey.Reconciliation(
                            retried.Id,
                            retried.DesiredGeneration)),
                    session,
                    ct);
            }
            else
            {
                await dbContext.StoreEventAsync(
                    OutboxWrite.Create(
                        retried.ToDeletionRequestedEvent(),
                        RuntimeCellPendingKey.Deletion(
                            retried.Id,
                            retried.DesiredGeneration)),
                    session,
                    ct);
            }
            return retried.ToDto();
        }, cancellationToken: cancellationToken);
    }
}

public sealed class RetryRuntimeCellRequestValidator
    : AbstractValidator<RetryRuntimeCellRequest>
{
    public RetryRuntimeCellRequestValidator()
    {
        RuleFor(request => request.ResourceGroupId)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Resource group ID must be a MongoDB ObjectId.");
        RuleFor(request => request.CellId)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Runtime cell ID must be a MongoDB ObjectId.");
    }
}

public static class RetryRuntimeCellEndpoint
{
    public static IEndpointRouteBuilder MapRetryRuntimeCellEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{ResourceGroupId}/runtime-cells/{CellId}/retry", async (
                IMediator mediator,
                [AsParameters] RetryRuntimeCellRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(cell => Results.Accepted(value: cell));
            })
            .WithName("RetryRuntimeCell")
            .WithSummary("Requests another attempt for a failed runtime cell.")
            .Produces<RuntimeCellDto>(StatusCodes.Status202Accepted);

        return app;
    }
}
