using MassTransit;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.ResourceGroups.Events;

[EntityName("create-resource-group")]
public record CreateEvent(
    string ResourceGroupId,
    string NamespaceName
) : BaseEvent;
