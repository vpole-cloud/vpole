using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Functions.Events;

[EntityName("function-app-created")]
public sealed record FunctionAppCreatedEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    string ResourceGroupId,
    string Name,
    string FunctionRoute,
    FunctionRevisionSpecSnapshot Spec) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("function-revision-created")]
public sealed record FunctionRevisionCreatedEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    FunctionRevisionSpecSnapshot Spec) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("function-revision-promoted")]
public sealed record FunctionRevisionPromotedEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    string? PreviousRevisionId) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("function-revision-failed")]
public sealed record FunctionRevisionFailedEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    string Code,
    string Message) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("function-revision-retired")]
public sealed record FunctionRevisionRetiredEvent(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

[EntityName("function-app-delete-requested")]
public sealed record FunctionAppDeleteRequestedEvent(
    string FunctionAppId,
    string? ActiveRevisionId) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
