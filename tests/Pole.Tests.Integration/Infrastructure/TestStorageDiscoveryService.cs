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
        Task.FromResult<IReadOnlyCollection<KubernetesPersistentVolumeDto>>([]);

    public Task<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>> GetPersistentVolumeClaimsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>([]);

    public Task<IReadOnlySet<string>> GetStorageClassNamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_storageClassNames);
}
