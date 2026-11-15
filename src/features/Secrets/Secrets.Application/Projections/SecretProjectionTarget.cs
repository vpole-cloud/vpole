using System.Net;
using System.Security.Cryptography;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Projections;

public interface ISecretProjectionTarget
{
    Task<SecretProjectionTargetOutcome> ApplyAsync(
        SecretProjection projection,
        Func<CancellationToken, Task<SecretProjectionManifestOutcome>> createManifest,
        CancellationToken ct);

    Task<SecretProjectionTargetOutcome> DeleteAsync(SecretProjection projection, CancellationToken ct);
}

internal union SecretProjectionClientOutcome(IKubernetes, SecretProjectionTargetFailure);

internal sealed record SecretProjectionRead(
    V1Secret? Secret
);

internal union SecretProjectionReadOutcome(SecretProjectionRead, SecretProjectionTargetFailure);

internal sealed class KubernetesSecretProjectionTarget(
    IKubernetesClientProvider clients,
    LoadSecretById loadSecret,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement
) : ISecretProjectionTarget
{
    public async Task<SecretProjectionTargetOutcome> ApplyAsync(
        SecretProjection projection,
        Func<CancellationToken, Task<SecretProjectionManifestOutcome>> createManifest,
        CancellationToken ct)
    {
        var resolved = await GetClientAsync(projection.DeploymentTargetId, ct);
        if (resolved.Value is SecretProjectionTargetFailure unavailable)
            return unavailable;
        var client = (IKubernetes)resolved.Value;
        var read = await ProtectReadAsync(() => ReadAsync(client, projection, ct), ct);
        if (read.Value is SecretProjectionTargetFailure readFailure)
            return readFailure;
        var current = ((SecretProjectionRead)read.Value).Secret;
        var authority = await loadSecret(projection.TenantId, projection.SecretId, ct);
        if (authority?.Revision != projection.DesiredRevision ||
            !authority.Bindings.Any(x => x.Id == projection.BindingId && authority.AllowsBinding(x)) ||
            !await HasAuthorizedConsumerAsync(authority, projection, ct))
            return new SecretProjectionDesiredStateChanged();
        if (current is not null && !IsOwned(current, projection))
            return new SecretProjectionOwnershipConflict();
        var prepared = await createManifest(ct);
        if (prepared.Value is SecretProjectionTargetFailure failure)
            return failure;
        var manifest = (V1Secret)prepared.Value;
        try
        {
            return await ProtectWriteAsync(
                async () =>
                {
                    if (current is null)
                    {
                        await client.CoreV1.CreateNamespacedSecretAsync(
                            manifest,
                            projection.KubernetesNamespace,
                            cancellationToken: ct);
                    }
                    else
                    {
                        manifest.Metadata.ResourceVersion = current.Metadata.ResourceVersion;
                        await client.CoreV1.ReplaceNamespacedSecretAsync(
                            manifest,
                            projection.KubernetesName,
                            projection.KubernetesNamespace,
                            cancellationToken: ct);
                    }
                },
                ct);
        }
        finally
        {
            foreach (var value in manifest.Data.Values)
                CryptographicOperations.ZeroMemory(value);
        }
    }

    public async Task<SecretProjectionTargetOutcome> DeleteAsync(SecretProjection projection, CancellationToken ct)
    {
        var resolved = await GetClientAsync(projection.DeploymentTargetId, ct);
        if (resolved.Value is SecretProjectionTargetFailure unavailable)
            return unavailable;
        var client = (IKubernetes)resolved.Value;
        var read = await ProtectReadAsync(() => ReadAsync(client, projection, ct), ct);
        if (read.Value is SecretProjectionTargetFailure readFailure)
            return readFailure;
        var current = ((SecretProjectionRead)read.Value).Secret;
        if (current is null)
            return new SecretProjectionTargetSucceeded();
        if (!IsOwned(current, projection))
            return new SecretProjectionOwnershipConflict();
        // Recheck authoritative bindings after the Kubernetes read. Metadata may itself be drifted.
        var authority = await loadSecret(projection.TenantId, projection.SecretId, ct);
        if (authority is not null && await HasAuthorizedConsumerAsync(authority, projection, ct))
            return new SecretProjectionTargetSucceeded();
        return await ProtectWriteAsync(
            async () =>
            {
                await client.CoreV1.DeleteNamespacedSecretAsync(
                    projection.KubernetesName,
                    projection.KubernetesNamespace,
                    body: new V1DeleteOptions
                    {
                        Preconditions = new V1Preconditions
                        {
                            Uid = current.Metadata.Uid,
                            ResourceVersion = current.Metadata.ResourceVersion,
                        },
                    },
                    cancellationToken: ct);
            },
            ct);
    }

    private async Task<SecretProjectionClientOutcome> GetClientAsync(string targetId, CancellationToken ct)
    {
        try
        {
            return await clients.GetAsync(targetId, ct);
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            return exception.Reason == DeploymentTargetUnavailableReason.Disabled
                ? new SecretProjectionTargetDisabled()
                : new SecretProjectionTargetUnavailable();
        }
    }

    private static SecretProjectionTargetFailure MapKubernetesFailure(HttpOperationException exception) =>
        exception.Response?.StatusCode switch
        {
            HttpStatusCode.NotFound => new SecretProjectionNamespaceUnavailable(),
            HttpStatusCode.Conflict => new SecretProjectionConcurrentWrite(),
            _ => new SecretProjectionRequestRejected(),
        };

    private static async Task<SecretProjectionReadOutcome> ProtectReadAsync(
        Func<Task<SecretProjectionRead>> operation,
        CancellationToken ct)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException exception)
        {
            // The SDK exception may own a decrypted HTTP body. Never retain it in the outcome.
            return MapKubernetesFailure(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException ||
                                          (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new SecretProjectionTransportUnavailable();
        }
    }

    private static async Task<SecretProjectionTargetOutcome> ProtectWriteAsync(
        Func<Task> operation,
        CancellationToken ct)
    {
        try
        {
            await operation();
            return new SecretProjectionTargetSucceeded();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException exception)
        {
            // The SDK exception may own a decrypted HTTP body. Never retain it in the outcome.
            return MapKubernetesFailure(exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException ||
                                          (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new SecretProjectionTransportUnavailable();
        }
    }

    private static string? MetadataValue(IDictionary<string, string>? values, string key) =>
        values is not null && values.TryGetValue(key, out var value) ? value : null;

    private async Task<bool> HasAuthorizedConsumerAsync(
        Secret secret,
        SecretProjection projection,
        CancellationToken ct)
    {
        if (!SecretProjectionAuthority.MatchesScope(secret, projection))
            return false;
        // A newly authorized binding for the same consumer must survive old cleanup.
        if (!secret.Bindings.Any(binding =>
                secret.AllowsBinding(binding) && SecretProjectionAuthority.MatchesConsumer(binding, projection) &&
                SecretProjectionAuthority.IncludesDeployment(binding.Selection, projection.DeploymentId)))
            return false;
        var placement = await loadPlacement(
            new LoadExactResourceDeploymentRuntimePlacementRequest(
                TenantId: projection.TenantId,
                ResourceType: ResourceTypeId.Create(projection.ConsumerType),
                ResourceId: projection.ConsumerId,
                DeploymentId: projection.DeploymentId),
            cancellationToken: ct);
        return placement is not null && SecretProjectionAuthority.MatchesPlacement(placement, projection);
    }

    private static bool IsOwned(V1Secret current, SecretProjection projection) =>
        MetadataValue(current.Metadata.Labels, "pole.io/tenant-id") == projection.TenantId &&
        MetadataValue(current.Metadata.Labels, "pole.io/secret-id") == projection.SecretId &&
        MetadataValue(current.Metadata.Labels, "pole.io/resource-deployment-id") == projection.DeploymentId;

    private static async Task<SecretProjectionRead> ReadAsync(
        IKubernetes client,
        SecretProjection projection,
        CancellationToken ct)
    {
        try
        {
            var current = await client.CoreV1.ReadNamespacedSecretAsync(
                projection.KubernetesName,
                projection.KubernetesNamespace,
                cancellationToken: ct);
            // Only metadata is needed for ownership and optimistic concurrency.
            if (current.Data is not null)
                foreach (var value in current.Data.Values)
                    CryptographicOperations.ZeroMemory(value);
            current.Data = null;
            return new SecretProjectionRead(current);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return new SecretProjectionRead(null);
        }
    }
}
