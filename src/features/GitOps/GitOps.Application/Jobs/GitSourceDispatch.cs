using Hangfire;
using MassTransit;
using Microsoft.Extensions.Logging;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Events;

namespace Pole.Features.GitOps.Application.Jobs;

public delegate void DispatchGitSource(GitSourceIdentity source);

public sealed class EnqueueGitSourceOnRequested(DispatchGitSource dispatch) : IConsumer<GitSourceWorkRequested>
{
    public Task Consume(ConsumeContext<GitSourceWorkRequested> context)
    {
        dispatch(new(context.Message.EventCore.TenantId, context.Message.SourceId));
        return Task.CompletedTask;
    }
}

public sealed class EnqueueGitSourceOnRequestedDefinition : ConsumerDefinition<EnqueueGitSourceOnRequested>
{
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<EnqueueGitSourceOnRequested> consumer,
        IRegistrationContext context) =>
        endpoint.UseMessageRetry(retry => retry.Intervals(TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)));
}

public sealed class GitSourceRecoveryScanner(
    GitSourceScheduling scheduling,
    DispatchGitSource dispatch,
    ILogger<GitSourceRecoveryScanner> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ScanAsync(CancellationToken ct)
    {
        foreach (var source in await scheduling.RecoverableSourcesAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                dispatch(source);
            }
            catch (Exception exception)
            {
                // The request/claim remains in Mongo. A lost Hangfire enqueue cannot lose source work.
                logger.LogError(exception, "Could not dispatch GitOps source {SourceId}", source.SourceId);
            }
        }
    }
}
