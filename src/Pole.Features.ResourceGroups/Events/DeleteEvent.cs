using System.Collections.Immutable;
using MassTransit;
using Pole.Domain.Models;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("delete-resource-group")]
public record DeleteEvent(
    string ResourceGroupId,
    string Name,
    string Description,
    string KubernetesNamespace,
    ImmutableList<ShortResourceInfo> Resources,
    ImmutableHashSet<string> Tags
) : BaseEvent;
