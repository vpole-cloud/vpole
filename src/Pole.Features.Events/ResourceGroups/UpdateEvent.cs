using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.ResourceGroups;

[EntityName("update-resource-group")]
public sealed record UpdateEvent(
    string ResourceGroupId
) : BaseEvent;
