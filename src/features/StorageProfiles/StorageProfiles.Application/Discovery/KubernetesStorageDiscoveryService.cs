using k8s;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Contracts;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public sealed class KubernetesStorageDiscoveryService(IKubernetesClientProvider kubernetesClientProvider)
    : IStorageDiscoveryService
{
    private static readonly string[] DefaultStorageClassAnnotationKeys =
    [
        "storageclass.kubernetes.io/is-default-class",
        "storageclass.beta.kubernetes.io/is-default-class"
    ];

    public async Task<Result<IReadOnlyCollection<KubernetesStorageClassDto>>> GetStorageClassesAsync(
        CancellationToken cancellationToken = default)
    {
        var clientResult = await ResolveKubernetesAsync(cancellationToken);
        if (!clientResult.TryGetValue(out var kubernetes, out var clientFailure))
            return clientFailure;

        try
        {
            var storageClasses = await kubernetes.StorageV1.ListStorageClassAsync(cancellationToken: cancellationToken);
            return storageClasses.Items
                .OrderBy(storageClass => storageClass.Metadata.Name, StringComparer.Ordinal)
                .Select(MapStorageClass)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failures.ExternalService("Unable to query Kubernetes storage classes.", "Kubernetes", exception.GetType().Name);
        }
    }

    public async Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>> GetPersistentVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        var clientResult = await ResolveKubernetesAsync(cancellationToken);
        if (!clientResult.TryGetValue(out var kubernetes, out var clientFailure))
            return clientFailure;

        try
        {
            var persistentVolumes = await kubernetes.CoreV1.ListPersistentVolumeAsync(cancellationToken: cancellationToken);
            return persistentVolumes.Items
                .OrderBy(volume => volume.Metadata.Name, StringComparer.Ordinal)
                .Select(MapPersistentVolume)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failures.ExternalService("Unable to query Kubernetes persistent volumes.", "Kubernetes", exception.GetType().Name);
        }
    }

    public async Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>> GetPersistentVolumeClaimsAsync(
        CancellationToken cancellationToken = default)
    {
        var clientResult = await ResolveKubernetesAsync(cancellationToken);
        if (!clientResult.TryGetValue(out var kubernetes, out var clientFailure))
            return clientFailure;

        try
        {
            var persistentVolumeClaims = await kubernetes.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(
                cancellationToken: cancellationToken);
            return persistentVolumeClaims.Items
                .OrderBy(claim => claim.Metadata.NamespaceProperty, StringComparer.Ordinal)
                .ThenBy(claim => claim.Metadata.Name, StringComparer.Ordinal)
                .Select(MapPersistentVolumeClaim)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failures.ExternalService("Unable to query Kubernetes persistent volume claims.", "Kubernetes", exception.GetType().Name);
        }
    }

    public async Task<Result<IReadOnlySet<string>>> GetStorageClassNamesAsync(CancellationToken cancellationToken = default)
    {
        var clientResult = await ResolveKubernetesAsync(cancellationToken);
        if (!clientResult.TryGetValue(out var kubernetes, out var clientFailure))
            return clientFailure;

        try
        {
            var storageClasses = await kubernetes.StorageV1.ListStorageClassAsync(cancellationToken: cancellationToken);
            return storageClasses.Items
                .Select(storageClass => storageClass.Metadata.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failures.ExternalService("Unable to query Kubernetes storage classes.", "Kubernetes", exception.GetType().Name);
        }
    }

    private async Task<Result<IKubernetes>> ResolveKubernetesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var kubernetes = await kubernetesClientProvider.GetDefaultAsync(cancellationToken);
            return new Success<IKubernetes>(kubernetes);
        }
        catch (DeploymentTargetUnavailableException exception)
        {
            return Failures.ExternalService(
                "Unable to resolve the default Kubernetes deployment target.",
                "Kubernetes",
                $"Deployment target unavailable ({exception.Reason}).");
        }
    }

    private static KubernetesStorageClassDto MapStorageClass(V1StorageClass storageClass)
    {
        var annotations = storageClass.Metadata.Annotations ?? new Dictionary<string, string>();

        return new KubernetesStorageClassDto(
            storageClass.Metadata.Name,
            storageClass.Provisioner,
            DefaultStorageClassAnnotationKeys.Any(key => IsDefaultStorageClass(annotations, key)),
            storageClass.ReclaimPolicy,
            storageClass.VolumeBindingMode,
            storageClass.AllowVolumeExpansion,
            new Dictionary<string, string>(storageClass.Parameters ?? new Dictionary<string, string>()),
            SafeMetadata(storageClass.Metadata.Labels, storageClass.Metadata.Annotations));
    }

    private static KubernetesPersistentVolumeDto MapPersistentVolume(V1PersistentVolume volume)
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

    private static KubernetesPersistentVolumeClaimDto MapPersistentVolumeClaim(V1PersistentVolumeClaim claim)
    {
        var requestedCapacity = claim.Spec.Resources?.Requests is not null &&
                                claim.Spec.Resources.Requests.TryGetValue("storage", out var requestedStorage)
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
        IDictionary<string, string>? annotations)
    {
        return new KubernetesStorageOwnerDto(
            GetMetadataValue(PoleStorageLabels.TenantId, labels, annotations),
            GetMetadataValue(PoleStorageLabels.ResourceGroupId, labels, annotations),
            GetMetadataValue(PoleStorageLabels.ResourceType, labels, annotations),
            GetMetadataValue(PoleStorageLabels.ResourceId, labels, annotations),
            GetMetadataValue(PoleStorageLabels.ResourceName, labels, annotations));
    }

    private static string? GetMetadataValue(
        string key,
        IDictionary<string, string>? labels,
        IDictionary<string, string>? annotations)
    {
        if (labels is not null && labels.TryGetValue(key, out var labelValue))
            return labelValue;

        if (annotations is not null && annotations.TryGetValue(key, out var annotationValue))
            return annotationValue;

        return null;
    }

    private static IReadOnlyDictionary<string, string> SafeMetadata(
        IDictionary<string, string>? labels,
        IDictionary<string, string>? annotations)
    {
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        AddSafeMetadata(labels, "label", metadata);
        AddSafeMetadata(annotations, "annotation", metadata);
        return metadata;
    }

    private static void AddSafeMetadata(
        IDictionary<string, string>? source,
        string prefix,
        IDictionary<string, string> metadata)
    {
        if (source is null)
            return;

        foreach (var (key, value) in source)
        {
            if (IsSafeMetadataKey(key))
                metadata[$"{prefix}:{key}"] = value;
        }
    }

    private static bool IsSafeMetadataKey(string key) =>
        key.StartsWith("pole.io/", StringComparison.Ordinal) ||
        key.StartsWith("app.kubernetes.io/", StringComparison.Ordinal) ||
        DefaultStorageClassAnnotationKeys.Contains(key, StringComparer.Ordinal);
}
