using Hangfire;
using MassTransit;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.Secrets.Events;

namespace Pole.Features.GitOps.Application.Jobs;

// A bounded persisted-snapshot retry loop, not a repository fetcher or a general source worker.
public sealed class SnapshotResumeRecoveryJob(ManualSnapshotSources sources, DeclarativePipeline pipeline)
{
    [DisableConcurrentExecution(timeoutInSeconds: SnapshotExecutionPolicy.RecoveryLeaseSeconds)]
    public Task RecoverAsync(CancellationToken ct = default) => RecoverTenantAsync(null, ct);
    public async Task RecoverTenantAsync(string? tenant, CancellationToken ct = default)
    {
        foreach (var execution in await sources.DueAsync(tenant, ct))
        {
            ct.ThrowIfCancellationRequested();
            // Execution authority is the accepted snapshot of a server-owned source in this tenant.
            // No credentials or user-supplied tenant/mutation context are serialized into jobs.
            var source = await sources.LoadAsync(execution.TenantId, execution.SourceId, ct);
            if (source is null || source.SourceUrl != execution.SourceUrl) continue;
            await pipeline.ResumeAsync(execution.ToContext(), ct, onlyIfDue: true);
        }
    }
}

public sealed class ResumeSnapshotOnSecretValueChanged(ManualSnapshotSources sources, SnapshotResumeRecoveryJob recovery)
    : IConsumer<SecretChangedEvent>
{
    public async Task HandleAsync(SecretChangedEvent message, CancellationToken ct = default)
    {
        if (message.Change is not SecretRotated || string.IsNullOrWhiteSpace(message.Name)) return;
        await sources.WakeSecretAsync(message.EventCore.TenantId, message.Name.Trim().ToLowerInvariant(), ct);
        await recovery.RecoverTenantAsync(message.EventCore.TenantId, ct);
    }
    public Task Consume(ConsumeContext<SecretChangedEvent> context) => HandleAsync(context.Message, context.CancellationToken);
}
