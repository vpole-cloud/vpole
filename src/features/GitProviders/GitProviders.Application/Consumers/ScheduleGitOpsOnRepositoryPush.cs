using MassTransit;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitProviders.Events;

namespace Pole.Features.GitProviders.Application.Consumers;

public sealed class ScheduleGitOpsOnRepositoryPush(
    IGitSourceScheduler scheduler
) : IConsumer<GitRepositoryPushReceived>
{
    public Task Consume(ConsumeContext<GitRepositoryPushReceived> context)
    {
        var message = context.Message;
        return scheduler.RequestRepositorySyncAsync(
            new RepositorySyncSignal(
                message.EventCore.TenantId,
                message.Provider,
                message.ConnectionId,
                message.ExternalRepositoryId,
                message.Branch,
                message.DeliveryId),
            context.CancellationToken);
    }
}
