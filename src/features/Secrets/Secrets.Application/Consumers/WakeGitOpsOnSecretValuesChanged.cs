using MassTransit;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Consumers;

public sealed class WakeGitOpsOnSecretValuesChanged(
    IGitSourceScheduler scheduler
) : IConsumer<SecretChangedEvent>
{
    public Task Consume(ConsumeContext<SecretChangedEvent> context)
    {
        var message = context.Message;
        if (message.Change is not SecretRotated)
            return Task.CompletedTask;

        return scheduler.WakeDependencyAsync(
            new GitDependencyWakeSignal(
                message.EventCore.TenantId,
                new ResourceAddress("Secret", message.Name),
                message.SecretId,
                $"secret/{message.SecretId}/{message.Revision}/{message.Version}"),
            context.CancellationToken);
    }
}
