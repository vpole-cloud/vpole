using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.ResourceGroups;

[EntityName("create-resource-group")]
public record CreateEvent(
    string ResourceGroupId,
    string NamespaceName
) : BaseEvent;
