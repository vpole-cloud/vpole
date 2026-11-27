// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Consumers;

/// <summary>Adapts WebServices deletion to the SourceBuilds-owned cleanup contribution.</summary>
public sealed class CleanupSourceBuildConfigurationConsumer(
    CleanupSourceBuildConfigurations cleanup
) : IConsumer<WebServiceDeletedEvent>
{
    public Task Consume(ConsumeContext<WebServiceDeletedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task HandleAsync(WebServiceDeletedEvent message, CancellationToken cancellationToken = default) => cleanup(
        message.EventCore.TenantId,
        message.WebServiceId,
        cancellationToken);
}
