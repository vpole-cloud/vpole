using MassTransit;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.ResourceGroups.Events;

[EntityName("delete-resource-group")]
public record DeleteEvent(
    string ResourceGroupId,
    string NamespaceName
) : BaseEvent;
