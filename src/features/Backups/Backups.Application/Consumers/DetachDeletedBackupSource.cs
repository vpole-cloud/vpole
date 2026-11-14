using MassTransit;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.Backups.Application.Consumers;

public sealed class DetachDeletedBackupSource(
    BackupSourceLifecycle lifecycle
) : IConsumer<ResourceUnregisteredEvent>
{
    public Task Consume(ConsumeContext<ResourceUnregisteredEvent> context) =>
        lifecycle.RetireAsync(context.Message, context.CancellationToken);
}
