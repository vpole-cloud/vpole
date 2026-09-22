using System.Net;
using System.Security.Cryptography;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Projections;

public interface ISecretProjectionTarget
{
    Task ApplyAsync(SecretProjection projection, Func<CancellationToken, Task<V1Secret>> createManifest, CancellationToken ct);
    Task DeleteAsync(SecretProjection projection, CancellationToken ct);
}

public enum SecretProjectionTargetFailure
{
    ProjectionUnavailable,
    TargetUnavailable,
    TargetDisabled,
    OwnershipConflict,
    DesiredStateChanged,
    NamespaceUnavailable,
    ConcurrentWrite,
    RequestRejected,
    TransportUnavailable
}

public sealed class SecretProjectionTargetUnavailableException(
    SecretProjectionTargetFailure reason = SecretProjectionTargetFailure.ProjectionUnavailable) : Exception
{
    public SecretProjectionTargetFailure Reason { get; } = reason;
}

internal sealed class KubernetesSecretProjectionTarget(
    IKubernetesClientProvider clients,
    LoadSecretById loadSecret,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement) : ISecretProjectionTarget
{
    public Task ApplyAsync(SecretProjection projection, Func<CancellationToken, Task<V1Secret>> createManifest, CancellationToken ct) =>
        ProtectAsync(() => ApplyCoreAsync(projection, createManifest, ct), ct);

    public Task DeleteAsync(SecretProjection projection, CancellationToken ct) =>
        ProtectAsync(() => DeleteCoreAsync(projection, ct), ct);

    private static async Task ProtectAsync(Func<Task> operation, CancellationToken ct)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            throw new SecretProjectionTargetUnavailableException(exception.Reason == DeploymentTargetUnavailableReason.Disabled
                ? SecretProjectionTargetFailure.TargetDisabled : SecretProjectionTargetFailure.TargetUnavailable);
        }
        catch (HttpOperationException exception)
        {
            // The SDK exception may own a decrypted HTTP body. Never retain it as an inner exception.
            throw new SecretProjectionTargetUnavailableException(exception.Response?.StatusCode switch
            {
                HttpStatusCode.NotFound => SecretProjectionTargetFailure.NamespaceUnavailable,
                HttpStatusCode.Conflict => SecretProjectionTargetFailure.ConcurrentWrite,
                _ => SecretProjectionTargetFailure.RequestRejected
            });
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException ||
                                          exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new SecretProjectionTargetUnavailableException(SecretProjectionTargetFailure.TransportUnavailable);
        }
    }

    private async Task ApplyCoreAsync(SecretProjection projection, Func<CancellationToken, Task<V1Secret>> createManifest, CancellationToken ct)
    {
        var client = await clients.GetAsync(projection.DeploymentTargetId, ct);
        var current = await ReadAsync(client, projection, ct);
        var authority = await loadSecret(projection.TenantId, projection.SecretId, ct);
        if (authority?.Revision != projection.DesiredRevision ||
            !authority.Bindings.Any(x => x.Id == projection.BindingId) ||
            !await HasAuthorizedConsumerAsync(authority, projection, ct))
            throw new SecretProjectionTargetUnavailableException(SecretProjectionTargetFailure.DesiredStateChanged);
        if (current is not null)
            EnsureOwned(current, projection);
        var manifest = await createManifest(ct);
        try
        {
            if (current is null)
                await client.CoreV1.CreateNamespacedSecretAsync(manifest, projection.KubernetesNamespace, cancellationToken: ct);
            else
            {
                manifest.Metadata.ResourceVersion = current.Metadata.ResourceVersion;
                await client.CoreV1.ReplaceNamespacedSecretAsync(manifest, projection.KubernetesName, projection.KubernetesNamespace, cancellationToken: ct);
            }
        }
        finally
        {
            foreach (var value in manifest.Data.Values)
                CryptographicOperations.ZeroMemory(value);
        }
    }

    private async Task DeleteCoreAsync(SecretProjection projection, CancellationToken ct)
    {
        var client = await clients.GetAsync(projection.DeploymentTargetId, ct);
        var current = await ReadAsync(client, projection, ct);
        if (current is null)
            return;
        EnsureOwned(current, projection);
        // Recheck authoritative bindings after the Kubernetes read. Metadata may itself be drifted.
        var authority = await loadSecret(projection.TenantId, projection.SecretId, ct);
        if (authority is not null && await HasAuthorizedConsumerAsync(authority, projection, ct))
            return;
        await client.CoreV1.DeleteNamespacedSecretAsync(
            projection.KubernetesName,
            projection.KubernetesNamespace,
            body: new V1DeleteOptions
            {
                Preconditions = new V1Preconditions
                {
                    Uid = current.Metadata.Uid,
                    ResourceVersion = current.Metadata.ResourceVersion
                }
            },
            cancellationToken: ct);
    }

    private static string? MetadataValue(IDictionary<string, string>? values, string key) =>
        values is not null && values.TryGetValue(key, out var value) ? value : null;

    private async Task<bool> HasAuthorizedConsumerAsync(Secret secret, SecretProjection projection, CancellationToken ct)
    {
        if (secret.ResourceGroupId != projection.ResourceGroupId || secret.IsolationScopeId != projection.IsolationScopeId ||
            !secret.Bindings.Any(binding => binding.ConsumerType == projection.ConsumerType && binding.ConsumerId == projection.ConsumerId &&
                (binding.Selection is AllSecretDeployments || binding.Selection is SelectedSecretDeployments selected && selected.DeploymentIds.Contains(projection.DeploymentId))))
            return false;
        var placement = await loadPlacement(projection.TenantId, ResourceTypeId.Create(projection.ConsumerType), projection.ConsumerId, projection.DeploymentId, ct);
        return placement is not null && placement.DesiredOperation != "Delete" &&
            placement.DeploymentTargetId == projection.DeploymentTargetId && placement.KubernetesNamespace == projection.KubernetesNamespace &&
            placement.RuntimeCellId == projection.RuntimeCellId && placement.IsolationScopeId == projection.IsolationScopeId;
    }

    private static void EnsureOwned(V1Secret current, SecretProjection projection)
    {
        if (MetadataValue(current.Metadata.Labels, "pole.io/tenant-id") != projection.TenantId ||
            MetadataValue(current.Metadata.Labels, "pole.io/secret-id") != projection.SecretId ||
            MetadataValue(current.Metadata.Labels, "pole.io/resource-deployment-id") != projection.DeploymentId)
            throw new SecretProjectionTargetUnavailableException(SecretProjectionTargetFailure.OwnershipConflict);
    }

    private static async Task<V1Secret?> ReadAsync(IKubernetes client, SecretProjection projection, CancellationToken ct)
    {
        try
        {
            var current = await client.CoreV1.ReadNamespacedSecretAsync(projection.KubernetesName, projection.KubernetesNamespace, cancellationToken: ct);
            // Only metadata is needed for ownership and optimistic concurrency.
            if (current.Data is not null)
                foreach (var value in current.Data.Values)
                    CryptographicOperations.ZeroMemory(value);
            current.Data = null;
            return current;
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
