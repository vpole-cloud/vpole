using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.SeaweedFS.Events;

public sealed record SeaweedFSServiceSpecSnapshot(
    string Mode,
    int SizeGb,
    string StorageProfileId
);

[EntityName("pole.seaweedfs/v1/seaweedfs-service-created")]
public sealed record SeaweedFSServiceCreatedEvent(
    string SeaweedFSId,
    string Name,
    string ResourceGroupId,
    SeaweedFSServiceSpecSnapshot Spec,
    ImmutableHashSet<string> Tags,
    long ResourceRevision,
    EventCore EventCore
) : IPoleEvent;
