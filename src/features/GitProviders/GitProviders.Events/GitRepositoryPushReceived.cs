using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.GitProviders.Events;

[EntityName("pole.git-providers/v1/repository-push-received")]
public sealed record GitRepositoryPushReceived(
    string DeliveryId,
    string Provider,
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    string HeadCommitHint,
    EventCore EventCore
) : IPoleEvent;
