// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Consumers;

public sealed class ReconcileSecretOnChanged(
    SecretProjectionReconciler reconciler
) : IConsumer<SecretChangedEvent>
{
    public Task Consume(ConsumeContext<SecretChangedEvent> context) => reconciler.ReconcileAsync(
        context.Message.EventCore.TenantId,
        context.Message.SecretId,
        context.CancellationToken);
}
