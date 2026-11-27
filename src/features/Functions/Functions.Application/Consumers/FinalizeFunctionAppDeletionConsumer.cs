// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.ResourceDeployments.Events;

namespace Pole.Features.Functions.Application.Consumers;

public sealed class FinalizeFunctionAppDeletionConsumer(
    FinalizeFunctionAppDeletion finalizeFunctionAppDeletion
) : IConsumer<ResourceDeploymentDeletedEvent>
{
    public async Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context)
    {
        var message = context.Message;
        if (!string.Equals(
                message.ResourceType,
                FunctionResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
            return;

        var result = await finalizeFunctionAppDeletion(
            message.EventCore.TenantId,
            message.ResourceId,
            message.ResourceDeploymentId,
            context.CancellationToken);
        if (result is FunctionAppDeletionFinalizationConflict conflict)
            throw new InvalidOperationException(
                $"Function App deletion finalization conflicted for '{conflict.FunctionAppId}'.");
    }
}
