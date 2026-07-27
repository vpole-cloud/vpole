using MassTransit;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Consumers;

public sealed class ReconcileRuntimeCellOnReconciliationRequestedConsumer(
    IResourceGroupDbContext dbContext,
    IRuntimeCellNamespaceManager namespaceManager,
    TimeProvider timeProvider)
    : IConsumer<RuntimeCellReconciliationRequestedEvent>
{
    public Task Consume(ConsumeContext<RuntimeCellReconciliationRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        RuntimeCellReconciliationRequestedEvent message,
        CancellationToken cancellationToken = default)
    {
        var runtimeCell = await dbContext.RuntimeCells
            .Find(cell =>
                cell.Id == message.RuntimeCellId &&
                cell.TenantId == message.EventCore.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (runtimeCell is null ||
            runtimeCell.DesiredGeneration != message.Generation ||
            runtimeCell.State is not ProvisioningRuntimeCellState provisioning ||
            provisioning.Generation != message.Generation)
        {
            return;
        }

        var namespaceResult = await namespaceManager.EnsureCreatedAsync(
            runtimeCell,
            cancellationToken);
        if (namespaceResult is ISuccess)
        {
            await PersistReadyAsync(runtimeCell, message.Generation, cancellationToken);
            return;
        }

        await PersistFailureAsync(
            runtimeCell,
            message.Generation,
            (Failure)namespaceResult,
            cancellationToken);
    }

    private async Task PersistReadyAsync(
        RuntimeCell runtimeCell,
        long generation,
        CancellationToken cancellationToken)
    {
        var ready = runtimeCell.MarkReady(
            generation,
            timeProvider.GetUtcNow().UtcDateTime);
        await dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var replacement = await dbContext.RuntimeCells.ReplaceOneAsync(
                session,
                CurrentStateFilter(
                    runtimeCell,
                    generation,
                    "provisioning"),
                ready,
                cancellationToken: ct);
            if (replacement.ModifiedCount == 0)
                return Result.Success();

            await dbContext.StoreEventAsync(ready.ToReadyEvent(), session, ct);
            return Result.Success();
        }, cancellationToken: cancellationToken);
    }

    private async Task PersistFailureAsync(
        RuntimeCell runtimeCell,
        long generation,
        Failure failure,
        CancellationToken cancellationToken)
    {
        var attemptedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var failed = runtimeCell.MarkFailed(
            generation,
            RuntimeCellOperation.Reconciliation,
            FailureCode(failure),
            "The Kubernetes namespace reconciliation attempt failed.",
            attemptedAtUtc,
            attemptedAtUtc.Add(RetryDelay(generation)));
        await dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var replacement = await dbContext.RuntimeCells.ReplaceOneAsync(
                session,
                CurrentStateFilter(
                    runtimeCell,
                    generation,
                    "provisioning"),
                failed,
                cancellationToken: ct);
            if (replacement.ModifiedCount == 0)
                return Result.Success();

            await dbContext.StoreEventAsync(
                failed.ToReconciliationFailedEvent(),
                session,
                ct);
            return Result.Success();
        }, cancellationToken: cancellationToken);
    }

    internal static FilterDefinition<RuntimeCell> CurrentStateFilter(
        RuntimeCell runtimeCell,
        long generation,
        string stateDiscriminator) =>
        Builders<RuntimeCell>.Filter.And(
            Builders<RuntimeCell>.Filter.Eq(cell => cell.Id, runtimeCell.Id),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.TenantId, runtimeCell.TenantId),
            Builders<RuntimeCell>.Filter.Eq(cell => cell.DesiredGeneration, generation),
            Builders<RuntimeCell>.Filter.Eq("State._t", stateDiscriminator));

    internal static string FailureCode(Failure failure) =>
        failure switch
        {
            ConflictFailure => "NamespaceOwnershipConflict",
            ExternalServiceFailure => "NamespaceOperationFailed",
            _ => "NamespaceOperationRejected"
        };

    internal static TimeSpan RetryDelay(long generation)
    {
        var exponent = (int)Math.Clamp(generation - 1, 0, 5);
        return TimeSpan.FromSeconds(Math.Min(30 * (1 << exponent), 900));
    }
}

public sealed class DeleteRuntimeCellOnDeletionRequestedConsumer(
    IResourceGroupDbContext dbContext,
    IRuntimeCellNamespaceManager namespaceManager,
    IDeploymentTargetReferenceAdmission referenceAdmission,
    IResourceGroupDeletionFinalizer deletionFinalizer,
    TimeProvider timeProvider)
    : IConsumer<RuntimeCellDeletionRequestedEvent>
{
    public Task Consume(ConsumeContext<RuntimeCellDeletionRequestedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        RuntimeCellDeletionRequestedEvent message,
        CancellationToken cancellationToken = default)
    {
        var runtimeCell = await dbContext.RuntimeCells
            .Find(cell =>
                cell.Id == message.RuntimeCellId &&
                cell.TenantId == message.EventCore.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (runtimeCell is null ||
            runtimeCell.DesiredGeneration != message.Generation ||
            runtimeCell.State is not DeletingRuntimeCellState deleting ||
            deleting.Generation != message.Generation)
        {
            return;
        }

        var namespaceResult = await namespaceManager.DeleteAsync(
            runtimeCell,
            cancellationToken);
        if (namespaceResult is not ISuccess)
        {
            await PersistFailureAsync(
                runtimeCell,
                message.Generation,
                (Failure)namespaceResult,
                cancellationToken);
            return;
        }

        var deleted = runtimeCell.MarkDeleted(
            message.Generation,
            timeProvider.GetUtcNow().UtcDateTime);
        var persistence = await dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var replacement = await dbContext.RuntimeCells.ReplaceOneAsync(
                session,
                ReconcileRuntimeCellOnReconciliationRequestedConsumer.CurrentStateFilter(
                    runtimeCell,
                    message.Generation,
                    "deleting"),
                deleted,
                cancellationToken: ct);
            if (replacement.ModifiedCount == 0)
                return Result.Success();

            var unregister = await referenceAdmission.UnregisterAsync(
                runtimeCell.DeploymentTargetId,
                new DeploymentTargetReferenceOwner(
                    ResourcePlacementService.ReferenceOwnerType,
                    runtimeCell.Id),
                session,
                ct);
            if (unregister is not ISuccess)
                return (Failure)unregister;

            await dbContext.StoreEventAsync(deleted.ToDeletedEvent(), session, ct);
            return Result.Success();
        }, cancellationToken: cancellationToken);

        if (persistence is ISuccess)
        {
            await deletionFinalizer.TryFinalizeAsync(
                runtimeCell.TenantId,
                runtimeCell.ResourceGroupId,
                runtimeCell.IsolationScopeId,
                cancellationToken);
        }
    }

    private async Task PersistFailureAsync(
        RuntimeCell runtimeCell,
        long generation,
        Failure failure,
        CancellationToken cancellationToken)
    {
        var attemptedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var failed = runtimeCell.MarkFailed(
            generation,
            RuntimeCellOperation.Deletion,
            ReconcileRuntimeCellOnReconciliationRequestedConsumer.FailureCode(failure),
            "The Kubernetes namespace deletion attempt failed.",
            attemptedAtUtc,
            attemptedAtUtc.Add(
                ReconcileRuntimeCellOnReconciliationRequestedConsumer.RetryDelay(generation)));
        await dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            await dbContext.RuntimeCells.ReplaceOneAsync(
                session,
                ReconcileRuntimeCellOnReconciliationRequestedConsumer.CurrentStateFilter(
                    runtimeCell,
                    generation,
                    "deleting"),
                failed,
                cancellationToken: ct);
            return Result.Success();
        }, cancellationToken: cancellationToken);
    }
}

public sealed class ReconcileRuntimeCellConsumerDefinition
    : ConsumerDefinition<ReconcileRuntimeCellOnReconciliationRequestedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ReconcileRuntimeCellOnReconciliationRequestedConsumer> consumerConfigurator,
        IRegistrationContext context) =>
        endpointConfigurator.UseMessageRetry(retry =>
            retry.Intervals(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)));
}

public sealed class DeleteRuntimeCellConsumerDefinition
    : ConsumerDefinition<DeleteRuntimeCellOnDeletionRequestedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DeleteRuntimeCellOnDeletionRequestedConsumer> consumerConfigurator,
        IRegistrationContext context) =>
        endpointConfigurator.UseMessageRetry(retry =>
            retry.Intervals(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)));
}
