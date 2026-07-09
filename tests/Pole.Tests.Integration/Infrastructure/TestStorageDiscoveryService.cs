using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class TestStorageDiscoveryService(params string[] storageClassNames) : IStorageDiscoveryService
{
    private readonly IReadOnlySet<string> _storageClassNames = storageClassNames.ToHashSet(StringComparer.Ordinal);

    public Task<IReadOnlyCollection<KubernetesStorageClassDto>> GetStorageClassesAsync(
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

        return Task.FromResult(storageClasses);
    }

    public Task<IReadOnlyCollection<KubernetesPersistentVolumeDto>> GetPersistentVolumesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<KubernetesPersistentVolumeDto>>(
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
        ]);

    public Task<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>> GetPersistentVolumeClaimsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>(
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
        ]);

    public Task<IReadOnlySet<string>> GetStorageClassNamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_storageClassNames);
}
