using System.Collections.Concurrent;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.ResultType;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestStorageDiscoveryService
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        _storageClassNamesByTarget;
    private readonly ConcurrentQueue<string> _requestedDeploymentTargetIds = [];

    public TestStorageDiscoveryService(params string[] storageClassNames)
        : this(new Dictionary<string, IReadOnlyCollection<string>>
        {
            [TestData.DefaultDeploymentTargetId] = storageClassNames
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

    public Task<Result<
        IReadOnlyCollection<KubernetesStorageClassDto>,
        KubernetesStorageDiscoveryUnavailable>> GetStorageClassesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var storageClassNamesResult = ResolveStorageClassNames(deploymentTargetId);
        if (!storageClassNamesResult.TryGetValue(out var storageClassNames, out var failure))
        {
            return Task.FromResult<Result<
                IReadOnlyCollection<KubernetesStorageClassDto>,
                KubernetesStorageDiscoveryUnavailable>>(
                new Err<KubernetesStorageDiscoveryUnavailable>(failure));
        }

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
        return Task.FromResult<Result<
            IReadOnlyCollection<KubernetesStorageClassDto>,
            KubernetesStorageDiscoveryUnavailable>>(
            new Ok<IReadOnlyCollection<KubernetesStorageClassDto>>(storageClasses));
    }

    public Task<Result<
        IReadOnlyCollection<KubernetesPersistentVolumeDto>,
        KubernetesStorageDiscoveryUnavailable>> GetPersistentVolumesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var storageClassNamesResult = ResolveStorageClassNames(deploymentTargetId);
        if (!storageClassNamesResult.TryGetValue(out _, out var failure))
        {
            return Task.FromResult<Result<
                IReadOnlyCollection<KubernetesPersistentVolumeDto>,
                KubernetesStorageDiscoveryUnavailable>>(
                new Err<KubernetesStorageDiscoveryUnavailable>(failure));
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
        return Task.FromResult<Result<
            IReadOnlyCollection<KubernetesPersistentVolumeDto>,
            KubernetesStorageDiscoveryUnavailable>>(
            new Ok<IReadOnlyCollection<KubernetesPersistentVolumeDto>>(persistentVolumes));
    }

    public Task<Result<
        IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>,
        KubernetesStorageDiscoveryUnavailable>>
        GetPersistentVolumeClaimsAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
    {
        var storageClassNamesResult = ResolveStorageClassNames(deploymentTargetId);
        if (!storageClassNamesResult.TryGetValue(out _, out var failure))
        {
            return Task.FromResult<Result<
                IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>,
                KubernetesStorageDiscoveryUnavailable>>(
                new Err<KubernetesStorageDiscoveryUnavailable>(failure));
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
                    TestData.TenantId,
                    "rg-1",
                    "Redis",
                    "redis-1",
                    "redis-main"))
        ];
        return Task.FromResult<Result<
            IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>,
            KubernetesStorageDiscoveryUnavailable>>(
            new Ok<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>(
                persistentVolumeClaims));
    }

    public Task<Result<IReadOnlySet<string>, KubernetesStorageDiscoveryUnavailable>>
        GetStorageClassNamesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ResolveStorageClassNames(deploymentTargetId));

    private Result<IReadOnlySet<string>, KubernetesStorageDiscoveryUnavailable>
        ResolveStorageClassNames(
        string deploymentTargetId)
    {
        _requestedDeploymentTargetIds.Enqueue(deploymentTargetId);
        return _storageClassNamesByTarget.TryGetValue(
            deploymentTargetId,
            out var storageClassNames)
            ? new Ok<IReadOnlySet<string>>(storageClassNames)
            : new Err<KubernetesStorageDiscoveryUnavailable>(
                new KubernetesStorageDiscoveryUnavailable(deploymentTargetId));
    }
}
