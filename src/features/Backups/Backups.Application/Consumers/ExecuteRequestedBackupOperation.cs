using Hangfire;
using MassTransit;
using Pole.Features.Backups.Application.Jobs;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Events;

namespace Pole.Features.Backups.Application.Consumers;

public sealed class ExecuteRequestedBackupOperation(
    ExecuteBackupOperation workflow,
    IBackgroundJobClient jobs,
    TimeProvider clock
) : IConsumer<BackupOperationWorkRequestedEvent>
{
    public async Task Consume(ConsumeContext<BackupOperationWorkRequestedEvent> context)
    {
        var message = context.Message;
        var tenantId = message.EventCore.TenantId;
        if (message.NotBeforeUtc > clock.GetUtcNow().UtcDateTime)
            jobs.Schedule<BackupOperationJob>(
                job => job.ExecuteAsync(tenantId, message.OperationId, CancellationToken.None),
                new DateTimeOffset(message.NotBeforeUtc));
        else
            await workflow.ExecuteAsync(tenantId, message.OperationId, context.CancellationToken);
    }
}
