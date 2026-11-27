// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.StorageProfiles.Events;

namespace Pole.Features.StorageProfiles.Application.Realtime;

public sealed class NotifyStorageObservationChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<StorageObservationChangedEvent>
{
    public Task Consume(ConsumeContext<StorageObservationChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task HandleAsync(StorageObservationChangedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange(
            "storage-observations",
            ResourceId: message.DeploymentTargetId,
            Revision: message.ConnectionVersion,
            ObservationVersion: message.ObservationVersion),
        ct);
}
