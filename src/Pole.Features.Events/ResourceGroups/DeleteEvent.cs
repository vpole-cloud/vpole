using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.ResourceGroups;

[EntityName("delete-resource-group")]
public record DeleteEvent(
    string ResourceGroupId,
    string NamespaceName
) : BaseEvent;
