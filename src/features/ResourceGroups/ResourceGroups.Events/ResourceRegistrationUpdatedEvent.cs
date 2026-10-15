using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("pole.resource-groups/v1/resource-registration-updated")]
public sealed record ResourceRegistrationUpdatedEvent(
    string ResourceGroupId,
    string ResourceType,
    string ResourceId) : IPoleEvent
{
    public EventCore EventCore
    {
        get;
        init;
    } = EventCore.Create();
}
