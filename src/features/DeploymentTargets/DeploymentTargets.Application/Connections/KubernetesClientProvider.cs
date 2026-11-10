using System.Collections.Concurrent;
using k8s;
using MongoDB.Bson;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Connections;

public delegate void InvalidateDeploymentTargetKubernetesClient(string deploymentTargetId);

public sealed class KubernetesClientProvider(
    LoadDeploymentTargetById loadDeploymentTargetById,
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    CreateDeploymentTargetKubernetesClient createDeploymentTargetKubernetesClient
) : IKubernetesClientProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _creationGates = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<IKubernetes> _retiredClients = [];
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var disposedClients = new HashSet<IKubernetes>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _clients.Values)
            if (disposedClients.Add(entry.Client))
                entry.Client.Dispose();
        while (_retiredClients.TryTake(out var retired))
            if (disposedClients.Add(retired))
                retired.Dispose();
    }

    public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default) =>
        GetClientAsync(deploymentTargetId, allowDisabled: false, cancellationToken);

    public Task<IKubernetes> GetForCleanupAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) => GetClientAsync(
        deploymentTargetId,
        allowDisabled: true,
        cancellationToken);

    public async Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var targetId = await loadDefaultDeploymentTargetId(cancellationToken);
        if (string.IsNullOrWhiteSpace(targetId))
            throw new DeploymentTargetUnavailableException(
                null,
                DeploymentTargetUnavailableReason.DefaultNotConfigured);

        return await GetAsync(targetId, cancellationToken);
    }

    private async Task<IKubernetes> GetClientAsync(
        string deploymentTargetId,
        bool allowDisabled,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!ObjectId.TryParse(deploymentTargetId, out _))
            throw new DeploymentTargetUnavailableException(
                deploymentTargetId,
                DeploymentTargetUnavailableReason.NotFound);

        var target = await LoadTargetAsync(deploymentTargetId, cancellationToken);
        EnsureAvailable(target, deploymentTargetId, allowDisabled);
        if (TryGetCurrent(target!, out var current))
            return current;

        var gate = _creationGates.GetOrAdd(deploymentTargetId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            target = await LoadTargetAsync(deploymentTargetId, cancellationToken);
            EnsureAvailable(target, deploymentTargetId, allowDisabled);
            if (TryGetCurrent(target!, out current))
                return current;

            var created = await createDeploymentTargetKubernetesClient(
                target!.Id,
                target.Connection,
                cancellationToken);
            if (created is StoredDeploymentTargetConnectionUnavailable)
                throw new DeploymentTargetUnavailableException(
                    deploymentTargetId,
                    DeploymentTargetUnavailableReason.ConnectionUnavailable);
            var client = (IKubernetes)created.Value;

            if (Volatile.Read(ref _disposed) != 0)
            {
                client.Dispose();
                throw new ObjectDisposedException(nameof(KubernetesClientProvider));
            }

            var replacement = new CacheEntry(target.Revision, target.Connection.Version, client);
            if (_clients.TryGetValue(deploymentTargetId, out var previous))
                _retiredClients.Add(previous.Client);
            _clients[deploymentTargetId] = replacement;
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(string deploymentTargetId)
    {
        if (_clients.TryRemove(deploymentTargetId, out var removed))
            _retiredClients.Add(removed.Client);
    }

    private async Task<DeploymentTarget?> LoadTargetAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken) => await loadDeploymentTargetById(deploymentTargetId, cancellationToken);

    private bool TryGetCurrent(DeploymentTarget target, out IKubernetes client)
    {
        if (_clients.TryGetValue(target.Id, out var cached) && cached.ConnectionVersion == target.Connection.Version)
        {
            // Metadata/admission revisions do not alter transport credentials. Record the
            // observed durable revision without churning sockets; connection rotation always
            // advances ConnectionVersion and therefore takes the rebuild path.
            if (cached.Revision != target.Revision)
                _clients.TryUpdate(target.Id, cached with { Revision = target.Revision }, cached);
            client = cached.Client;
            return true;
        }

        client = null!;
        return false;
    }

    private void EnsureAvailable(
        DeploymentTarget? target,
        string deploymentTargetId,
        bool allowDisabled)
    {
        if (target is null)
            throw new DeploymentTargetUnavailableException(
                deploymentTargetId,
                DeploymentTargetUnavailableReason.NotFound);
        if (target.Enabled || allowDisabled)
            return;

        Invalidate(deploymentTargetId);
        throw new DeploymentTargetUnavailableException(deploymentTargetId, DeploymentTargetUnavailableReason.Disabled);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record CacheEntry(
        long Revision,
        long ConnectionVersion,
        IKubernetes Client
    );
}
