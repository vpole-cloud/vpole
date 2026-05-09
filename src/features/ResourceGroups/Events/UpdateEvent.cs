using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("update-resource-group")]
public sealed record UpdateEvent(
    string ResourceGroupId,
    string Name,
    string Description,
    string KubernetesNamespace,
    ImmutableList<ShortResourceInfo> Resources,
    UpdateEventChanges Changes
) : BaseEvent;

public sealed record UpdateEventChanges(
    UpdateLog<string> Description,
    UpdateLog<ImmutableHashSet<string>> Tags);
