// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;

namespace Pole.Features.Outbox.Application;

public interface IOutboxPublisher
{
    Task PublishAsync(
        object message,
        Type messageType,
        CancellationToken cancellationToken);
}

public sealed class MassTransitOutboxPublisher(
    IPublishEndpoint publishEndpoint
) : IOutboxPublisher
{
    public Task PublishAsync(
        object message,
        Type messageType,
        CancellationToken cancellationToken) => publishEndpoint.Publish(message, messageType, cancellationToken);
}
