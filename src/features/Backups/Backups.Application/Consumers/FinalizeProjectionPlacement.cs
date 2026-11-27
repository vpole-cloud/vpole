// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Database;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;

namespace Pole.Features.Backups.Application.Consumers;

public sealed class FinalizeProjectionPlacement(
    MongoRunTransaction transactions,
    PurgeExactDeletedResourceDeployment purge
) : IConsumer<ResourceDeploymentDeletedEvent>
{
    public async Task Consume(ConsumeContext<ResourceDeploymentDeletedEvent> context)
    {
        var message = context.Message;
        var type = BackupResourceTypes.Destination;
        if (message.ResourceType != type.Value)
            return;
        await transactions.InvokeOutcomeAsync<bool>(
            async (tx, ct) =>
            {
                var result = await purge(
                    new(message.EventCore.TenantId, type, message.ResourceId, message.ResourceDeploymentId),
                    tx.Session,
                    ct);
                return result is ResourceDeploymentsPurged ? tx.Commit(true) : tx.Abort(false);
            },
            context.CancellationToken);
    }
}
