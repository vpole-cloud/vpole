// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceDeployments.Events;

namespace Pole.Features.ResourceDeployments.Application.Realtime;

public sealed class ResourceDeploymentObservationConsumer(
    PublishRealtimeChange publish
) : IConsumer<ResourceDeploymentObservationChangedEvent>
{
    public Task Consume(ConsumeContext<ResourceDeploymentObservationChangedEvent> context)
    {
        var message = context.Message;
        return publish(
            new RealtimeChange(
                "runtime-observations",
                message.EventCore.TenantId,
                message.DeploymentId,
                message.ResourceGroupId,
                message.DeploymentRevision,
                message.DesiredGeneration,
                message.ObservationVersion),
            context.CancellationToken);
    }
}
