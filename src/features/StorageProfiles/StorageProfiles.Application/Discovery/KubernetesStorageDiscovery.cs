using k8s;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Discovery;

internal union ResolveStorageDiscoveryClientOutcome(
    IKubernetes,
    KubernetesStorageDiscoveryUnavailable);

internal static class KubernetesStorageDiscovery
{
    private static readonly string[] DefaultStorageClassAnnotationKeys =
    [
        "storageclass.kubernetes.io/is-default-class",
        "storageclass.beta.kubernetes.io/is-default-class"
    ];

    public static async Task<ResolveStorageDiscoveryClientOutcome>
        ResolveAsync(
            IKubernetesClientProvider kubernetesClientProvider,
            string deploymentTargetId,
            CancellationToken cancellationToken)
    {
        try
        {
            return await kubernetesClientProvider.GetAsync(
                deploymentTargetId,
                cancellationToken);
        }
        catch (DeploymentTargetUnavailableException)
        {
            return new KubernetesStorageDiscoveryUnavailable(deploymentTargetId);
        }
    }

    public static KubernetesStorageClassDto MapStorageClass(V1StorageClass storageClass)
    {
        var annotations = storageClass.Metadata.Annotations ?? new Dictionary<string, string>();

        return new KubernetesStorageClassDto(
            storageClass.Metadata.Name,
            storageClass.Provisioner,
            DefaultStorageClassAnnotationKeys.Any(key => IsDefaultStorageClass(annotations, key)),
            storageClass.ReclaimPolicy,
            storageClass.VolumeBindingMode,
            storageClass.AllowVolumeExpansion,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
    }

    public static KubernetesPersistentVolumeDto MapPersistentVolume(V1PersistentVolume volume)
    {
        var capacity = volume.Spec.Capacity is not null &&
                       volume.Spec.Capacity.TryGetValue("storage", out var storage)
            ? storage.ToString()
            : null;

        return new KubernetesPersistentVolumeDto(
            volume.Metadata.Name,
            capacity,
            volume.Status?.Phase,
            volume.Spec.StorageClassName,
            volume.Spec.ClaimRef?.NamespaceProperty,
            volume.Spec.ClaimRef?.Name,
            volume.Spec.PersistentVolumeReclaimPolicy,
            OwnerFromMetadata(volume.Metadata.Labels, volume.Metadata.Annotations));
    }

    public static KubernetesPersistentVolumeClaimDto MapPersistentVolumeClaim(
        V1PersistentVolumeClaim claim)
    {
        var requestedCapacity = claim.Spec.Resources?.Requests is not null &&
                                claim.Spec.Resources.Requests.TryGetValue(
                                    "storage",
                                    out var requestedStorage)
            ? requestedStorage.ToString()
            : null;

        var capacity = claim.Status?.Capacity is not null &&
                       claim.Status.Capacity.TryGetValue("storage", out var storage)
            ? storage.ToString()
            : null;

        return new KubernetesPersistentVolumeClaimDto(
            claim.Metadata.Name,
            claim.Metadata.NamespaceProperty,
            capacity,
            requestedCapacity,
            claim.Status?.Phase,
            claim.Spec.StorageClassName,
            claim.Spec.VolumeName,
            OwnerFromMetadata(claim.Metadata.Labels, claim.Metadata.Annotations));
    }

    private static bool IsDefaultStorageClass(IDictionary<string, string> annotations, string key) =>
        annotations.TryGetValue(key, out var value) &&
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static KubernetesStorageOwnerDto OwnerFromMetadata(
        IDictionary<string, string>? labels,
        IDictionary<string, string>? annotations) =>
        new(
            StorageOwnerValueRules.MongoIdentity(GetMetadataValue(PoleStorageLabels.TenantId, labels, annotations)),
            StorageOwnerValueRules.MongoIdentity(GetMetadataValue(PoleStorageLabels.ResourceGroupId,
                labels,
                annotations)),
            StorageOwnerValueRules.OptionalText(GetMetadataValue(PoleStorageLabels.ResourceType, annotations, labels),
                253),
            StorageOwnerValueRules.ResourceIdentity(GetMetadataValue(PoleStorageLabels.ResourceId,
                labels,
                annotations)),
            StorageOwnerValueRules.OptionalText(GetMetadataValue(PoleStorageLabels.ResourceName, labels, annotations),
                253));

    private static string? GetMetadataValue(
        string key,
        IDictionary<string, string>? primary,
        IDictionary<string, string>? fallback)
    {
        if (primary is not null && primary.TryGetValue(key, out var primaryValue))
            return primaryValue;

        return fallback is not null && fallback.TryGetValue(key, out var fallbackValue)
            ? fallbackValue
            : null;
    }
}
