using System.Collections.Concurrent;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;

namespace Pole.Testing;

public sealed class TestStorageDiscoveryService
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        _storageClassNamesByTarget;
    private readonly ConcurrentQueue<string> _requestedDeploymentTargetIds = [];

    public TestStorageDiscoveryService(params string[] storageClassNames)
        : this(new Dictionary<string, IReadOnlyCollection<string>>
        {
            [TestPlatformDefaults.DeploymentTargetId] = storageClassNames
        })
    {
    }

    public TestStorageDiscoveryService(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> storageClassNamesByTarget)
    {
        _storageClassNamesByTarget = storageClassNamesByTarget.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)pair.Value.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> RequestedDeploymentTargetIds =>
        _requestedDeploymentTargetIds.ToArray();

    public Task<DiscoverKubernetesStorageClassesOutcome> GetStorageClassesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var storageClassNamesResult = ResolveStorageClassNames(deploymentTargetId);
        if (storageClassNamesResult is KubernetesStorageDiscoveryUnavailable failure)
        {
            return Task.FromResult<DiscoverKubernetesStorageClassesOutcome>(
                failure);
        }

        var storageClassNames = (IReadOnlySet<string>)storageClassNamesResult.Value;
        IReadOnlyCollection<KubernetesStorageClassDto> storageClasses = storageClassNames
            .Order(StringComparer.Ordinal)
            .Select((name, index) => new KubernetesStorageClassDto(
                name,
                "test.provisioner",
                index == 0,
                "Delete",
                "WaitForFirstConsumer",
                true,
                new Dictionary<string, string>(),
                new Dictionary<string, string>()))
            .ToArray();
        return Task.FromResult<DiscoverKubernetesStorageClassesOutcome>(
            storageClasses);
    }

    public Task<DiscoverKubernetesPersistentVolumesOutcome> GetPersistentVolumesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var storageClassNamesResult = ResolveStorageClassNames(deploymentTargetId);
        if (storageClassNamesResult is KubernetesStorageDiscoveryUnavailable failure)
        {
            return Task.FromResult<DiscoverKubernetesPersistentVolumesOutcome>(
                failure);
        }

        IReadOnlyCollection<KubernetesPersistentVolumeDto> persistentVolumes =
        [
            new(
                "pv-standard-1",
                "20Gi",
                "Available",
                "standard",
                null,
                null,
                "Retain",
                new KubernetesStorageOwnerDto(null, null, null, null, null))
        ];
        return Task.FromResult<DiscoverKubernetesPersistentVolumesOutcome>(
            persistentVolumes);
    }

    public Task<DiscoverKubernetesPersistentVolumeClaimsOutcome>
        GetPersistentVolumeClaimsAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
    {
        var storageClassNamesResult = ResolveStorageClassNames(deploymentTargetId);
        if (storageClassNamesResult is KubernetesStorageDiscoveryUnavailable failure)
        {
            return Task.FromResult<DiscoverKubernetesPersistentVolumeClaimsOutcome>(
                failure);
        }

        IReadOnlyCollection<KubernetesPersistentVolumeClaimDto> persistentVolumeClaims =
        [
            new(
                "redis-data-redis-main-0",
                "test-alpha",
                "5Gi",
                "5Gi",
                "Bound",
                "standard",
                "pv-standard-1",
                new KubernetesStorageOwnerDto(
                    TestPlatformDefaults.TenantId,
                    "rg-1",
                    "Redis",
                    "redis-1",
                    "redis-main"))
        ];
        return Task.FromResult<DiscoverKubernetesPersistentVolumeClaimsOutcome>(
            persistentVolumeClaims);
    }

    public Task<DiscoverKubernetesStorageClassNamesOutcome>
        GetStorageClassNamesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ResolveStorageClassNames(deploymentTargetId));

    private DiscoverKubernetesStorageClassNamesOutcome
        ResolveStorageClassNames(
        string deploymentTargetId)
    {
        _requestedDeploymentTargetIds.Enqueue(deploymentTargetId);
        return _storageClassNamesByTarget.TryGetValue(
            deploymentTargetId,
            out var storageClassNames)
            ? storageClassNames
            : new KubernetesStorageDiscoveryUnavailable(deploymentTargetId);
    }
}
