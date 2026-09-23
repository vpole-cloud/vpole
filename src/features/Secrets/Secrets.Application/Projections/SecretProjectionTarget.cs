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
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Projections;

public interface ISecretProjectionTarget
{
    Task<Result<Unit, SecretProjectionTargetError>> ApplyAsync(
        SecretProjection projection,
        Func<CancellationToken, Task<Result<V1Secret, SecretProjectionTargetError>>> createManifest,
        CancellationToken ct);
    Task<Result<Unit, SecretProjectionTargetError>> DeleteAsync(SecretProjection projection, CancellationToken ct);
}

internal sealed class KubernetesSecretProjectionTarget(
    IKubernetesClientProvider clients,
    LoadSecretById loadSecret,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement) : ISecretProjectionTarget
{
    private async Task<Result<IKubernetes, SecretProjectionTargetError>> GetClientAsync(string targetId, CancellationToken ct)
    {
        try
        {
            return new Ok<IKubernetes>(await clients.GetAsync(targetId, ct));
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            return new Err<SecretProjectionTargetError>(exception.Reason == DeploymentTargetUnavailableReason.Disabled
                ? new SecretProjectionTargetDisabled() : new SecretProjectionTargetUnavailable());
        }
    }

    private static async Task<Result<T, SecretProjectionTargetError>> ProtectKubernetesAsync<T>(
        Func<Task<T>> operation,
        CancellationToken ct) where T : notnull
    {
        try
        {
            return new Ok<T>(await operation());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpOperationException exception)
        {
            // The SDK exception may own a decrypted HTTP body. Never retain it in the result.
            return new Err<SecretProjectionTargetError>(exception.Response?.StatusCode switch
            {
                HttpStatusCode.NotFound => new SecretProjectionNamespaceUnavailable(),
                HttpStatusCode.Conflict => new SecretProjectionConcurrentWrite(),
                _ => new SecretProjectionRequestRejected()
            });
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException ||
                                          exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new Err<SecretProjectionTargetError>(new SecretProjectionTransportUnavailable());
        }
    }

    public async Task<Result<Unit, SecretProjectionTargetError>> ApplyAsync(
        SecretProjection projection,
        Func<CancellationToken, Task<Result<V1Secret, SecretProjectionTargetError>>> createManifest,
        CancellationToken ct)
    {
        var resolved = await GetClientAsync(projection.DeploymentTargetId, ct);
        if (resolved is Err<SecretProjectionTargetError> unavailable)
            return unavailable;
        var client = ((Ok<IKubernetes>)resolved.Value).Value;
        var read = await ProtectKubernetesAsync(() => ReadAsync(client, projection, ct), ct);
        if (read is Err<SecretProjectionTargetError> readFailure)
            return readFailure;
        var current = ((Ok<SecretRead>)read.Value).Value.Secret;
        var authority = await loadSecret(projection.TenantId, projection.SecretId, ct);
        if (authority?.Revision != projection.DesiredRevision ||
            !authority.Bindings.Any(x => x.Id == projection.BindingId) ||
            !await HasAuthorizedConsumerAsync(authority, projection, ct))
            return new Err<SecretProjectionTargetError>(new SecretProjectionDesiredStateChanged());
        if (current is not null && !IsOwned(current, projection))
            return new Err<SecretProjectionTargetError>(new SecretProjectionOwnershipConflict());
        var prepared = await createManifest(ct);
        if (prepared is Err<SecretProjectionTargetError> failure)
            return failure;
        var manifest = ((Ok<V1Secret>)prepared.Value).Value;
        try
        {
            return await ProtectKubernetesAsync(async () =>
            {
                if (current is null)
                    await client.CoreV1.CreateNamespacedSecretAsync(manifest, projection.KubernetesNamespace, cancellationToken: ct);
                else
                {
                    manifest.Metadata.ResourceVersion = current.Metadata.ResourceVersion;
                    await client.CoreV1.ReplaceNamespacedSecretAsync(manifest, projection.KubernetesName, projection.KubernetesNamespace, cancellationToken: ct);
                }
                return Unit.Value;
            }, ct);
        }
        finally
        {
            foreach (var value in manifest.Data.Values)
                CryptographicOperations.ZeroMemory(value);
        }
    }

    public async Task<Result<Unit, SecretProjectionTargetError>> DeleteAsync(SecretProjection projection, CancellationToken ct)
    {
        var resolved = await GetClientAsync(projection.DeploymentTargetId, ct);
        if (resolved is Err<SecretProjectionTargetError> unavailable)
            return unavailable;
        var client = ((Ok<IKubernetes>)resolved.Value).Value;
        var read = await ProtectKubernetesAsync(() => ReadAsync(client, projection, ct), ct);
        if (read is Err<SecretProjectionTargetError> readFailure)
            return readFailure;
        var current = ((Ok<SecretRead>)read.Value).Value.Secret;
        if (current is null)
            return new Ok<Unit>(Unit.Value);
        if (!IsOwned(current, projection))
            return new Err<SecretProjectionTargetError>(new SecretProjectionOwnershipConflict());
        // Recheck authoritative bindings after the Kubernetes read. Metadata may itself be drifted.
        var authority = await loadSecret(projection.TenantId, projection.SecretId, ct);
        if (authority is not null && await HasAuthorizedConsumerAsync(authority, projection, ct))
            return new Ok<Unit>(Unit.Value);
        return await ProtectKubernetesAsync(async () =>
        {
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
            return Unit.Value;
        }, ct);
    }

    private static string? MetadataValue(IDictionary<string, string>? values, string key) =>
        values is not null && values.TryGetValue(key, out var value) ? value : null;

    private async Task<bool> HasAuthorizedConsumerAsync(Secret secret, SecretProjection projection, CancellationToken ct)
    {
        if (!SecretProjectionAuthority.MatchesScope(secret, projection))
            return false;
        // A newly authorized binding for the same consumer must survive old cleanup.
        if (!secret.Bindings.Any(binding =>
                SecretProjectionAuthority.MatchesConsumer(binding, projection) &&
                SecretProjectionAuthority.IncludesDeployment(binding.Selection, projection.DeploymentId)))
            return false;
        var placement = await loadPlacement(projection.TenantId, ResourceTypeId.Create(projection.ConsumerType), projection.ConsumerId, projection.DeploymentId, ct);
        return placement is not null && SecretProjectionAuthority.MatchesPlacement(placement, projection);
    }

    private static bool IsOwned(V1Secret current, SecretProjection projection) =>
        MetadataValue(current.Metadata.Labels, "pole.io/tenant-id") == projection.TenantId &&
        MetadataValue(current.Metadata.Labels, "pole.io/secret-id") == projection.SecretId &&
        MetadataValue(current.Metadata.Labels, "pole.io/resource-deployment-id") == projection.DeploymentId;

    private sealed record SecretRead(V1Secret? Secret);

    private static async Task<SecretRead> ReadAsync(IKubernetes client, SecretProjection projection, CancellationToken ct)
    {
        try
        {
            var current = await client.CoreV1.ReadNamespacedSecretAsync(projection.KubernetesName, projection.KubernetesNamespace, cancellationToken: ct);
            // Only metadata is needed for ownership and optimistic concurrency.
            if (current.Data is not null)
                foreach (var value in current.Data.Values)
                    CryptographicOperations.ZeroMemory(value);
            current.Data = null;
            return new SecretRead(current);
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return new SecretRead(null);
        }
    }
}
