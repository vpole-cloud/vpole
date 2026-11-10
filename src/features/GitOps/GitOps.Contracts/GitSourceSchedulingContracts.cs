namespace Pole.Features.GitOps.Contracts;

public sealed record RepositorySyncSignal(
    string TenantId,
    string Provider,
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    string SignalId
);

public sealed record GitDependencyWakeSignal(
    string TenantId,
    ResourceAddress Address,
    string ResourceId,
    string SignalId
);

// Integration adapters own their events. GitOps receives only durable, tenant-scoped scheduling intents.
public interface IGitSourceScheduler
{
    Task RequestRepositorySyncAsync(RepositorySyncSignal signal, CancellationToken cancellationToken);
    Task WakeDependencyAsync(GitDependencyWakeSignal signal, CancellationToken cancellationToken);
}
