using System.Collections.Concurrent;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.ResultType;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestStorageDiscoveryService : IStorageDiscoveryService
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

    public Task<Result<IReadOnlyCollection<KubernetesStorageClassDto>>> GetStorageClassesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var storageClassNamesResult = ResolveStorageClassNames(deploymentTargetId);
        if (!storageClassNamesResult.TryGetValue(out var storageClassNames, out var failure))
        {
            return Task.FromResult<Result<IReadOnlyCollection<KubernetesStorageClassDto>>>(
                failure);
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
        return Task.FromResult<Result<IReadOnlyCollection<KubernetesStorageClassDto>>>(
            Result.Success(storageClasses));
    }

    public Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>> GetPersistentVolumesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var storageClassNamesResult = ResolveStorageClassNames(deploymentTargetId);
        if (!storageClassNamesResult.TryGetValue(out _, out var failure))
        {
            return Task.FromResult<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>>(
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
        return Task.FromResult<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>>(
            Result.Success(persistentVolumes));
    }

    public Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>>
        GetPersistentVolumeClaimsAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
    {
        var storageClassNamesResult = ResolveStorageClassNames(deploymentTargetId);
        if (!storageClassNamesResult.TryGetValue(out _, out var failure))
        {
            return Task.FromResult<
                Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>>(
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
                    TestData.TenantId,
                    "rg-1",
                    "Redis",
                    "redis-1",
                    "redis-main"))
        ];
        return Task.FromResult<
            Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>>(
            Result.Success(persistentVolumeClaims));
    }

    public Task<Result<IReadOnlySet<string>>> GetStorageClassNamesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ResolveStorageClassNames(deploymentTargetId));

    private Result<IReadOnlySet<string>> ResolveStorageClassNames(
        string deploymentTargetId)
    {
        _requestedDeploymentTargetIds.Enqueue(deploymentTargetId);
        return _storageClassNamesByTarget.TryGetValue(
            deploymentTargetId,
            out var storageClassNames)
            ? Result.Success(storageClassNames)
            : Failures.ExternalService(
                "Unable to resolve test storage classes for the deployment target.",
                "Kubernetes",
                "Deployment target unavailable.");
    }
}
