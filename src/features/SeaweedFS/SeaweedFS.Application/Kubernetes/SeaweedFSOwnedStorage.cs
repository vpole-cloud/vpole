using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Pole.Features.SeaweedFS.Application.Kubernetes;

internal sealed class SeaweedFSOwnedStorage(
    IKubernetes client,
    SeaweedFSClusterManifestOptions options
)
{
    private bool Owns(V1ObjectMeta metadata) => options.Identity
        .ToDictionary()
        .All(pair => metadata.Labels?.TryGetValue(pair.Key, out var value) == true && value == pair.Value);

    private static V1DeleteOptions Preconditions(V1ObjectMeta metadata) => new()
    {
        Preconditions = new V1Preconditions { ResourceVersion = metadata.ResourceVersion, Uid = metadata.Uid },
    };

    public async Task<V1Secret> EnsureSecretAsync(V1Secret desired, CancellationToken ct)
    {
        try
        {
            var existing = await client.CoreV1.ReadNamespacedSecretAsync(
                desired.Metadata.Name,
                options.Namespace,
                cancellationToken: ct);
            if (!Owns(existing.Metadata) || existing.Type != desired.Type)
                throw new InvalidOperationException("SeaweedFS Secret ownership or type changed.");
            return existing;
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound)
        {
            // A racing create conflicts and retries through deployment reconciliation; never replace its password.
            return await client.CoreV1.CreateNamespacedSecretAsync(desired, options.Namespace, cancellationToken: ct);
        }
    }

    public async Task<bool> SupportsAutomaticReclaimAsync(CancellationToken ct)
    {
        var storageClass = await client.StorageV1.ReadStorageClassAsync(
            options.Storage.StorageClassName,
            cancellationToken: ct);
        return storageClass.ReclaimPolicy is null or "Delete";
    }

    public async Task<bool> CleanupAsync(CancellationToken ct)
    {
        var selector = string.Join(",", options.Identity.ToDictionary().Select(pair => $"{pair.Key}={pair.Value}"));
        var claims = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(
            options.Namespace,
            labelSelector: selector,
            cancellationToken: ct);
        foreach (var claim in claims.Items)
        {
            if (!Owns(claim.Metadata))
                throw new InvalidOperationException("SeaweedFS PVC ownership changed.");
            if (claim.Metadata.DeletionTimestamp is null)
                await client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(
                    claim.Metadata.Name,
                    options.Namespace,
                    body: Preconditions(claim.Metadata),
                    cancellationToken: ct);
        }

        var cleared = claims.Items.Count == 0;
        foreach (var name in new[]
                 {
                     options.BootstrapS3SecretName, options.FilerConfigurationSecretName,
                     options.MetadataCredentialsSecretName
                 })
        {
            try
            {
                var secret = await client.CoreV1.ReadNamespacedSecretAsync(
                    name,
                    options.Namespace,
                    cancellationToken: ct);
                if (!Owns(secret.Metadata))
                    throw new InvalidOperationException("SeaweedFS Secret ownership changed.");
                await client.CoreV1.DeleteNamespacedSecretAsync(
                    name,
                    options.Namespace,
                    body: Preconditions(secret.Metadata),
                    cancellationToken: ct);
                cleared = false;
            }
            catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }

        if (!cleared)
            return false;
        // Both CSI and in-tree provisioners can finish reclaim after a PVC disappears.
        // Poll only; Kubernetes and its provisioner own deletion of the backing volume.
        var volumes = await client.CoreV1.ListPersistentVolumeAsync(cancellationToken: ct);
        return !volumes.Items.Any(volume => volume.Spec.ClaimRef?.NamespaceProperty == options.Namespace &&
            BelongsToInstance(volume.Spec.ClaimRef.Name));
    }

    private bool BelongsToInstance(string claimName)
    {
        // StatefulSet claim names consist of the template name, StatefulSet name and ordinal.
        // CNPG uses its cluster name followed by a numeric instance identifier.
        var name = options.Name;
        return new[]
        {
            $"{name}-master-{name}-master-", $"{name}-filer-{name}-filer-",
            $"mount0-{name}-volume-", $"{name}-metadata-"
        }.Any(prefix => claimName.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(claimName.AsSpan(prefix.Length), out _));
    }
}
