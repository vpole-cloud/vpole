using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.ResourceGroups.Application.Common;

internal static class RuntimeCellEventExtensions
{
    public static RuntimeCellReconciliationRequestedEvent ToReconciliationRequestedEvent(this RuntimeCell cell)
    {
        var state = RequiredState<ProvisioningRuntimeCellState>(cell);
        return new RuntimeCellReconciliationRequestedEvent(cell.Id, state.Generation)
        {
            EventCore = EventCore.Create(cell.TenantId)
        };
    }

    public static RuntimeCellReadyEvent ToReadyEvent(this RuntimeCell cell)
    {
        var state = RequiredState<ReadyRuntimeCellState>(cell);
        return new RuntimeCellReadyEvent(cell.Id, state.Generation)
        {
            EventCore = EventCore.Create(cell.TenantId)
        };
    }

    public static RuntimeCellReconciliationFailedEvent ToReconciliationFailedEvent(this RuntimeCell cell)
    {
        var state = RequiredState<FailedRuntimeCellState>(cell);
        if (state.Operation != RuntimeCellOperation.Reconciliation)
        {
            throw new InvalidOperationException(
                "Only reconciliation failures can produce a runtime-cell reconciliation-failed event.");
        }

        return new RuntimeCellReconciliationFailedEvent(cell.Id, state.Generation, state.FailureCode)
        {
            EventCore = EventCore.Create(cell.TenantId)
        };
    }

    public static RuntimeCellDeletionRequestedEvent ToDeletionRequestedEvent(this RuntimeCell cell)
    {
        var state = RequiredState<DeletingRuntimeCellState>(cell);
        return new RuntimeCellDeletionRequestedEvent(cell.Id, state.Generation, state.NextCheckAtUtc, state.CheckSequence)
        {
            EventCore = EventCore.Create(cell.TenantId)
        };
    }

    public static RuntimeCellDeletedEvent ToDeletedEvent(this RuntimeCell cell)
    {
        var state = RequiredState<DeletedRuntimeCellState>(cell);
        return new RuntimeCellDeletedEvent(cell.Id, state.Generation)
        {
            EventCore = EventCore.Create(cell.TenantId)
        };
    }

    private static TState RequiredState<TState>(RuntimeCell cell)
        where TState : RuntimeCellState
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.State is not TState state || state.Generation != cell.DesiredGeneration)
        {
            throw new InvalidOperationException(
                $"Runtime cell '{cell.Id}' is not in the expected {typeof(TState).Name} state.");
        }

        return state;
    }
}
