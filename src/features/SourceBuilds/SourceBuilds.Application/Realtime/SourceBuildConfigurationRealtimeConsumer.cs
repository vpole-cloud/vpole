// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Realtime.Contracts;
using Pole.Features.SourceBuilds.Events;

namespace Pole.Features.SourceBuilds.Application.Realtime;

public sealed class NotifySourceBuildConfigurationChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<SourceBuildConfigurationChangedEvent>
{
    public Task Consume(ConsumeContext<SourceBuildConfigurationChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task HandleAsync(SourceBuildConfigurationChangedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange("source-builds", message.EventCore.TenantId, message.WebServiceId),
        ct);
}
