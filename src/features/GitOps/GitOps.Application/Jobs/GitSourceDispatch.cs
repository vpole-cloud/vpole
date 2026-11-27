// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Hangfire;
using MassTransit;
using Microsoft.Extensions.Logging;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Events;

namespace Pole.Features.GitOps.Application.Jobs;

public delegate void DispatchGitSource(GitSourceIdentity source);

public delegate void ScheduleGitSourceAt(GitSourceIdentity source, DateTime notBeforeUtc);

public sealed class EnqueueGitSourceOnRequested(
    DispatchGitSource dispatch,
    ScheduleGitSourceAt schedule,
    ILogger<EnqueueGitSourceOnRequested> logger
) : IConsumer<GitSourceWorkRequested>
{
    public Task Consume(ConsumeContext<GitSourceWorkRequested> context)
    {
        var source = new GitSourceIdentity(context.Message.EventCore.TenantId, context.Message.SourceId);
        if (context.Message.NotBeforeUtc is { } notBeforeUtc)
        {
            schedule(source, notBeforeUtc);
            logger.LogInformation(
                "GitOps work scheduled for source {SourceId}; notBeforeUtc={NotBeforeUtc:O}, scheduledAtUtc={ScheduledAtUtc:O}",
                source.SourceId,
                notBeforeUtc,
                DateTime.UtcNow);
        }
        else
        {
            dispatch(source);
        }

        return Task.CompletedTask;
    }
}

public sealed class EnqueueGitSourceOnRequestedDefinition : ConsumerDefinition<EnqueueGitSourceOnRequested>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<EnqueueGitSourceOnRequested> consumer,
        IRegistrationContext context) => endpoint.UseMessageRetry(retry => retry.Intervals(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)));
}

public sealed class GitSourceRecoveryScanner(
    GitSourceScheduling scheduling,
    DispatchGitSource dispatch,
    ILogger<GitSourceRecoveryScanner> logger
)
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
