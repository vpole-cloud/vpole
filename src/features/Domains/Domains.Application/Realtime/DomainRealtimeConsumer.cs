// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Domains.Events;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Domains.Application.Realtime;

public sealed class NotifyDomainMetadataChangedConsumer(
    PublishRealtimeChange publish
) : IConsumer<DomainMetadataChangedEvent>
{
    public Task Consume(ConsumeContext<DomainMetadataChangedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task HandleAsync(DomainMetadataChangedEvent message, CancellationToken ct = default) => publish(
        new RealtimeChange("domains", message.EventCore.TenantId),
        ct);
}
