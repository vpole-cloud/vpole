using MassTransit;
using Pole.Features.Core.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("delete-resource-group")]
public record DeleteEvent(
    string ResourceGroupId,
    string NamespaceName
) : BaseEvent;
