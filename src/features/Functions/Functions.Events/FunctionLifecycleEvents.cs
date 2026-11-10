using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Functions.Events;

[EntityName("pole.functions/v1/function-app-created")]
public sealed record FunctionAppCreatedEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    string ResourceGroupId,
    FunctionPlacementSnapshot Placement,
    string Name,
    string FunctionRoute,
    FunctionRevisionSpecSnapshot Spec
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.functions/v1/function-revision-created")]
public sealed record FunctionRevisionCreatedEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    FunctionPlacementSnapshot Placement,
    FunctionRevisionSpecSnapshot Spec
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.functions/v1/function-revision-promoted")]
public sealed record FunctionRevisionPromotedEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    FunctionPlacementSnapshot Placement,
    string? PreviousRevisionId
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.functions/v1/function-revision-failed")]
public sealed record FunctionRevisionFailedEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    FunctionPlacementSnapshot Placement,
    string Code,
    string Message
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.functions/v1/function-revision-retired")]
public sealed record FunctionRevisionRetiredEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    FunctionPlacementSnapshot Placement
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("pole.functions/v1/function-app-delete-requested")]
public sealed record FunctionAppDeleteRequestedEvent(
    string FunctionAppId,
    FunctionPlacementSnapshot Placement,
    string? ActiveRevisionId
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record FunctionPlacementSnapshot(
    string ResourceDeploymentId,
    string RuntimeCellId,
    string DeploymentTargetId
);
