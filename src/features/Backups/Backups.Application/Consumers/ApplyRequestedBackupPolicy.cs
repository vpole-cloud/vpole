// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Hangfire;
using MassTransit;
using Pole.Features.Backups.Application.Jobs;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Events;

namespace Pole.Features.Backups.Application.Consumers;

public sealed class ApplyRequestedBackupPolicy(
    ApplyBackupPolicy workflow,
    IBackgroundJobClient jobs,
    TimeProvider clock
) : IConsumer<BackupPolicyWorkRequestedEvent>
{
    public async Task Consume(ConsumeContext<BackupPolicyWorkRequestedEvent> context)
    {
        var message = context.Message;
        var tenantId = message.EventCore.TenantId;
        if (message.NotBeforeUtc > clock.GetUtcNow().UtcDateTime)
            jobs.Schedule<BackupPolicyJob>(
                job => job.ExecuteAsync(tenantId, message.PolicyId, CancellationToken.None),
                new DateTimeOffset(message.NotBeforeUtc));
        else
            await workflow.ExecuteAsync(tenantId, message.PolicyId, context.CancellationToken);
    }
}
