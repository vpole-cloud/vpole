using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("pole.resource-groups/v1/runtime-cell-reconciliation-requested")]
public sealed record RuntimeCellReconciliationRequestedEvent(
    string RuntimeCellId,
    long Generation
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.resource-groups/v1/runtime-cell-ready")]
public sealed record RuntimeCellReadyEvent(
    string RuntimeCellId,
    long Generation
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.resource-groups/v1/runtime-cell-reconciliation-failed")]
public sealed record RuntimeCellReconciliationFailedEvent(
    string RuntimeCellId,
    long Generation,
    string FailureCode
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.resource-groups/v1/runtime-cell-deletion-requested")]
public sealed record RuntimeCellDeletionRequestedEvent(
    string RuntimeCellId,
    long Generation,
    DateTime? NotBeforeUtc,
    long CheckSequence
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.resource-groups/v1/runtime-cell-deleted")]
public sealed record RuntimeCellDeletedEvent(
    string RuntimeCellId,
    long Generation
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
