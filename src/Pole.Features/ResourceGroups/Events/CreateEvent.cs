using MassTransit;
using Pole.Features.Core.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("create-resource-group")]
public record CreateEvent(
    string ResourceGroupId,
    string NamespaceName
) : BaseEvent;
