using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceConnections.Application.Persistence;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceConnections.Application.Kubernetes;

public sealed record ResourceConnectionPolicySucceeded;

public sealed record ResourceConnectionPolicyDesiredStateChanged;

public sealed record ResourceConnectionPolicyTargetUnavailable;
public sealed record ResourceConnectionPolicyBaselineUnavailable;
public sealed record ResourceConnectionPolicyNamespaceUnavailable;
public sealed record ResourceConnectionPolicyOwnershipConflict;
public sealed record ResourceConnectionPolicyConcurrentWrite;
public sealed record ResourceConnectionPolicyRequestRejected;
public sealed record ResourceConnectionPolicyTransportUnavailable;

public union ResourceConnectionPolicyApplyOutcome(
    ResourceConnectionPolicySucceeded,
    ResourceConnectionPolicyDesiredStateChanged,
    ResourceConnectionPolicyTargetUnavailable,
    ResourceConnectionPolicyBaselineUnavailable,
    ResourceConnectionPolicyNamespaceUnavailable,
    ResourceConnectionPolicyOwnershipConflict,
    ResourceConnectionPolicyConcurrentWrite,
    ResourceConnectionPolicyRequestRejected,
    ResourceConnectionPolicyTransportUnavailable);

public union ResourceConnectionPolicyDeleteOutcome(
    ResourceConnectionPolicySucceeded,
    ResourceConnectionPolicyDesiredStateChanged,
    ResourceConnectionPolicyTargetUnavailable,
    ResourceConnectionPolicyOwnershipConflict,
    ResourceConnectionPolicyConcurrentWrite,
    ResourceConnectionPolicyRequestRejected,
    ResourceConnectionPolicyTransportUnavailable);

public interface IResourceConnectionPolicyTarget
{
    Task<ResourceConnectionPolicyApplyOutcome> ApplyAsync(
        ResourceConnection connection,
        ConnectionTargetSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<ResourceConnectionPolicyDeleteOutcome> DeleteAsync(
        ResourceConnection connection,
        ConnectionTargetSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<ResourceConnectionPolicyDeleteOutcome> DeleteTombstoneAsync(
        ResourceConnectionCleanupTombstone tombstone,
        ConnectionTargetSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

internal sealed class KubernetesResourceConnectionPolicyTarget(
    IKubernetesClientProvider clients,
    EnsureRuntimeCellIngressBaseline ensureBaseline,
    LoadResourceConnection loadConnection,
    LoadResourceConnectionTombstone loadTombstone)
    : IResourceConnectionPolicyTarget
{
    public async Task<ResourceConnectionPolicyApplyOutcome> ApplyAsync(
        ResourceConnection connection,
        ConnectionTargetSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var baseline = await ensureBaseline(
                new EnsureRuntimeCellIngressBaselineRequest(
                    TenantId: connection.TenantId,
                    RuntimeCellId: snapshot.DestinationRuntimeCellId,
                    DeploymentTargetId: snapshot.TargetId,
                    KubernetesNamespace: snapshot.DestinationNamespace),
                cancellationToken: cancellationToken);
            var baselineFailure = BaselineFailure(baseline);
            if (baselineFailure is { } failure)
                return failure;

            var client = await clients.GetAsync(snapshot.TargetId, cancellationToken);
            var existing = await ReadAsync(
                client,
                connection.Id,
                snapshot.DestinationNamespace,
                cancellationToken);
            if (existing is not null && !ResourceConnectionPolicyManifestBuilder.IsOwned(
                    existing,
                    connection.TenantId,
                    connection.Id,
                    snapshot.PolicyLocation))
                return new ResourceConnectionPolicyOwnershipConflict();

            // The read and baseline call can take time. Check the Mongo authority
            // immediately before a cluster write; a later change is repaired by
            // its own outbox request and the periodic recovery scan.
            if (!await IsCurrentApplyAsync(connection, snapshot, cancellationToken))
                return new ResourceConnectionPolicyDesiredStateChanged();

            var desired = ResourceConnectionPolicyManifestBuilder.Build(connection, snapshot);
            if (existing is not null &&
                ResourceConnectionPolicyManifestBuilder.MatchesDesired(existing, desired))
                return new ResourceConnectionPolicySucceeded();

            V1NetworkPolicy written;
            if (existing is null)
            {
                written = await client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                    desired,
                    snapshot.DestinationNamespace,
                    cancellationToken: cancellationToken);
            }
            else
            {
                desired.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
                written = await client.NetworkingV1.ReplaceNamespacedNetworkPolicyAsync(
                    desired,
                    ResourceConnectionPolicyManifestBuilder.PolicyName(connection.Id),
                    snapshot.DestinationNamespace,
                    cancellationToken: cancellationToken);
            }
            if (!await IsCurrentApplyAsync(connection, snapshot, cancellationToken))
            {
                // A stale create can complete after a newer delete saw 404. The
                // preconditions remove only this exact write, never a replacement
                // from a newer generation. The durable tombstone also retries it.
                if (string.IsNullOrWhiteSpace(written.Metadata?.Uid) ||
                    string.IsNullOrWhiteSpace(written.Metadata.ResourceVersion))
                    return new ResourceConnectionPolicyRequestRejected();
                try
                {
                    await client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
                        ResourceConnectionPolicyManifestBuilder.PolicyName(connection.Id),
                        snapshot.DestinationNamespace,
                        body: new V1DeleteOptions
                        {
                            Preconditions = new V1Preconditions
                            {
                                Uid = written.Metadata.Uid,
                                ResourceVersion = written.Metadata.ResourceVersion
                            }
                        },
                        cancellationToken: cancellationToken);
                }
                catch (HttpOperationException exception)
                    when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
                {
                    // The newer cleanup already removed this exact object.
                }
                return new ResourceConnectionPolicyDesiredStateChanged();
            }
            return new ResourceConnectionPolicySucceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeploymentTargetUnavailableException)
        {
            return new ResourceConnectionPolicyTargetUnavailable();
        }
        catch (HttpOperationException exception)
        {
            return ApplyFailureForHttp(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException ||
                                          exception is OperationCanceledException)
        {
            return new ResourceConnectionPolicyTransportUnavailable();
        }
    }

    public Task<ResourceConnectionPolicyDeleteOutcome> DeleteAsync(
        ResourceConnection connection,
        ConnectionTargetSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        DeletePolicyAsync(
            connection.TenantId,
            connection.Id,
            snapshot,
            ct => IsCurrentCleanupAsync(connection, snapshot, ct),
            cancellationToken);

    public Task<ResourceConnectionPolicyDeleteOutcome> DeleteTombstoneAsync(
        ResourceConnectionCleanupTombstone tombstone,
        ConnectionTargetSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        DeletePolicyAsync(
            tombstone.TenantId,
            tombstone.Id,
            snapshot,
            ct => IsCurrentTombstoneAsync(tombstone, snapshot, ct),
            cancellationToken);

    private async Task<ResourceConnectionPolicyDeleteOutcome> DeletePolicyAsync(
        string tenantId,
        string connectionId,
        ConnectionTargetSnapshot snapshot,
        Func<CancellationToken, Task<bool>> isAuthorized,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await clients.GetAsync(snapshot.TargetId, cancellationToken);
            var existing = await ReadAsync(
                client,
                connectionId,
                snapshot.DestinationNamespace,
                cancellationToken);
            if (existing is null)
                return new ResourceConnectionPolicySucceeded();
            if (!ResourceConnectionPolicyManifestBuilder.IsOwned(
                    existing,
                    tenantId,
                    connectionId,
                    snapshot.PolicyLocation))
                return new ResourceConnectionPolicyOwnershipConflict();
            if (!await isAuthorized(cancellationToken))
                return new ResourceConnectionPolicyDesiredStateChanged();

            await client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
                ResourceConnectionPolicyManifestBuilder.PolicyName(connectionId),
                snapshot.DestinationNamespace,
                body: new V1DeleteOptions
                {
                    Preconditions = new V1Preconditions
                    {
                        Uid = existing.Metadata.Uid,
                        ResourceVersion = existing.Metadata.ResourceVersion
                    }
                },
                cancellationToken: cancellationToken);
            return new ResourceConnectionPolicySucceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeploymentTargetUnavailableException)
        {
            return new ResourceConnectionPolicyTargetUnavailable();
        }
        catch (HttpOperationException exception)
        {
            return DeleteFailureForHttp(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException ||
                                          exception is OperationCanceledException)
        {
            return new ResourceConnectionPolicyTransportUnavailable();
        }
    }

    private async Task<bool> IsCurrentApplyAsync(
        ResourceConnection expected,
        ConnectionTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var current = await loadConnection(expected.TenantId, expected.Id, cancellationToken);
        return current?.Generation == expected.Generation &&
            current.Lifecycle is ActiveResourceConnection active &&
            active.Targets.Any(target => target.Snapshot.HasSameResolvedValues(snapshot));
    }

    private async Task<bool> IsCurrentCleanupAsync(
        ResourceConnection expected,
        ConnectionTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var current = await loadConnection(expected.TenantId, expected.Id, cancellationToken);
        if (current?.Generation != expected.Generation)
            return false;
        return current.Lifecycle switch
        {
            ActiveResourceConnection active =>
                !active.Targets.Any(target => target.Snapshot.PolicyLocation == snapshot.PolicyLocation) &&
                active.RetiringTargets.Any(target => target.Snapshot.PolicyLocation == snapshot.PolicyLocation),
            DeletingResourceConnection deleting => deleting.Targets.Any(target =>
                target.Snapshot.PolicyLocation == snapshot.PolicyLocation)
        };
    }

    private async Task<bool> IsCurrentTombstoneAsync(
        ResourceConnectionCleanupTombstone expected,
        ConnectionTargetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var current = await loadTombstone(
            expected.TenantId,
            expected.Id,
            cancellationToken);
        return current?.Generation == expected.Generation &&
            current.Targets.Any(target => target.HasSameResolvedValues(snapshot));
    }

    private static async Task<V1NetworkPolicy?> ReadAsync(
        k8s.IKubernetes client,
        string connectionId,
        string namespaceName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                ResourceConnectionPolicyManifestBuilder.PolicyName(connectionId),
                namespaceName,
                cancellationToken: cancellationToken);
        }
        catch (HttpOperationException exception)
            when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static ResourceConnectionPolicyApplyOutcome ApplyFailureForHttp(
        HttpOperationException exception) => exception.Response?.StatusCode switch
        {
            HttpStatusCode.NotFound => new ResourceConnectionPolicyNamespaceUnavailable(),
            HttpStatusCode.Conflict => new ResourceConnectionPolicyConcurrentWrite(),
            _ => new ResourceConnectionPolicyRequestRejected()
        };

    private static ResourceConnectionPolicyApplyOutcome? BaselineFailure(
        EnsureRuntimeCellIngressBaselineOutcome outcome) => outcome switch
        {
            RuntimeCellIngressBaselineReady => null,
            RuntimeCellIngressBaselineTargetNotFound or
            RuntimeCellIngressBaselineTargetDisabled or
            RuntimeCellIngressBaselineDefaultTargetNotConfigured or
            RuntimeCellIngressBaselineTargetConnectionUnavailable =>
                new ResourceConnectionPolicyTargetUnavailable(),
            RuntimeCellIngressBaselineNotReady or
            RuntimeCellIngressBaselinePlacementMismatch or
            RuntimeCellIngressBaselineNamespaceOwnershipConflict or
            RuntimeCellIngressBaselineNamespaceUnavailable or
            RuntimeCellIngressBaselinePolicyOwnershipConflict or
            RuntimeCellIngressBaselinePolicyUnavailable =>
                new ResourceConnectionPolicyBaselineUnavailable()
        };

    private static ResourceConnectionPolicyDeleteOutcome DeleteFailureForHttp(
        HttpOperationException exception) => exception.Response?.StatusCode switch
        {
            HttpStatusCode.NotFound => new ResourceConnectionPolicySucceeded(),
            HttpStatusCode.Conflict => new ResourceConnectionPolicyConcurrentWrite(),
            _ => new ResourceConnectionPolicyRequestRejected()
        };
}
