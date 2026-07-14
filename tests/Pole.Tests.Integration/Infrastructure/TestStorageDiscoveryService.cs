using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.ResultType;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestStorageDiscoveryService(params string[] storageClassNames) : IStorageDiscoveryService
{
    private readonly IReadOnlySet<string> _storageClassNames = storageClassNames.ToHashSet(StringComparer.Ordinal);

    public Task<Result<IReadOnlyCollection<KubernetesStorageClassDto>>> GetStorageClassesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<KubernetesStorageClassDto> storageClasses = _storageClassNames
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

        return Task.FromResult<Result<IReadOnlyCollection<KubernetesStorageClassDto>>>(Result.Success(storageClasses));
    }

    public Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeDto>>> GetPersistentVolumesAsync(
        CancellationToken cancellationToken = default)
    {
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

    public Task<Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>> GetPersistentVolumeClaimsAsync(
        CancellationToken cancellationToken = default)
    {
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
        return Task.FromResult<Result<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>>(
            Result.Success(persistentVolumeClaims));
    }

    public Task<Result<IReadOnlySet<string>>> GetStorageClassNamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<IReadOnlySet<string>>>(Result.Success(_storageClassNames));
}
