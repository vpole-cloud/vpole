using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.FeatureName.Events;

[EntityName("pole.feature-route/v1/resource-created")]
public sealed record ResourceNameCreatedEvent(
    string ResourceId,
    string Name,
    long Revision,
    EventCore EventCore
) : IPoleEvent;

[EntityName("pole.feature-route/v1/resource-updated")]
public sealed record ResourceNameUpdatedEvent(
    string ResourceId,
    string Name,
    long Revision,
    EventCore EventCore
) : IPoleEvent;
