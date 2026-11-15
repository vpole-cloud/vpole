using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.SeaweedFS.Events;

[EntityName("pole.seaweedfs/v1/seaweedfs-service-changed")]
public sealed record SeaweedFSServiceChangedEvent(
    string SeaweedFSId,
    string Name,
    string ResourceGroupId,
    long ResourceRevision,
    string Operation,
    EventCore EventCore,
    string? BucketId = null
) : IPoleEvent;
