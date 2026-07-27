using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("runtime-cell-reconciliation-requested")]
public sealed record RuntimeCellReconciliationRequestedEvent(
    string RuntimeCellId,
    long Generation) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("runtime-cell-ready")]
public sealed record RuntimeCellReadyEvent(
    string RuntimeCellId,
    long Generation) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("runtime-cell-reconciliation-failed")]
public sealed record RuntimeCellReconciliationFailedEvent(
    string RuntimeCellId,
    long Generation,
    string FailureCode) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("runtime-cell-deletion-requested")]
public sealed record RuntimeCellDeletionRequestedEvent(
    string RuntimeCellId,
    long Generation) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("runtime-cell-deleted")]
public sealed record RuntimeCellDeletedEvent(
    string RuntimeCellId,
    long Generation) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
