using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.SeaweedFS.Events;

[EntityName("pole.seaweedfs/v1/credentials-revealed")]
public sealed record SeaweedFSCredentialsRevealedEvent(
    string SeaweedFSId,
    string? BucketId,
    string ActorUserId,
    EventCore EventCore
) : IPoleEvent;
