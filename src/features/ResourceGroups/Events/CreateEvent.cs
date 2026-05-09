using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("create-resource-group")]
public record CreateEvent(
    string ResourceGroupId,
    string Name,
    string Description,
    string KubernetesNamespace,
    ImmutableList<ShortResourceInfo> Resources,
    ImmutableHashSet<string> Tags
) : BaseEvent;
