using Hangfire;
using MassTransit;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Jobs;
using Pole.Features.Backups.Events;

namespace Pole.Features.Backups.Application.Consumers;

public sealed class ObserveRequestedBackupHistory(
    ObserveBackupArchiveHistory workflow,
    IBackgroundJobClient jobs,
    TimeProvider clock
) : IConsumer<BackupHistoryWorkRequestedEvent>
{
    public async Task Consume(ConsumeContext<BackupHistoryWorkRequestedEvent> context)
    {
        var message = context.Message;
        var tenantId = message.EventCore.TenantId;
        if (message.NotBeforeUtc > clock.GetUtcNow().UtcDateTime)
            jobs.Schedule<BackupArchiveHistoryJob>(
                job => job.ExecuteAsync(tenantId, message.ArchiveId, CancellationToken.None),
                new DateTimeOffset(message.NotBeforeUtc));
        else
            await workflow.ExecuteAsync(tenantId, message.ArchiveId, context.CancellationToken);
    }
}
