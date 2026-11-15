using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Common;

public sealed record SeaweedFSRequestedCapacity(
    long CpuMillis,
    long MemoryMi,
    int WorkloadInstances,
    long StorageGb
);

public sealed record SeaweedFSTopology(
    int Masters,
    int Volumes,
    int Filers,
    int Gateways,
    int MetadataInstances
)
{
    public const int MasterDiskGb = 1;
    public const int MetadataDiskGb = 1;
    public const int SingleFilerDiskGb = 1;

    public static SeaweedFSTopology For(SeaweedFSMode mode) => mode switch
    {
        SeaweedFSMode.SingleNode => new(1, 1, 1, 1, 0),
        SeaweedFSMode.Replicated => new(3, 3, 3, 3, 3),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static SeaweedFSRequestedCapacity Calculate(
        SeaweedFSMode mode,
        long cpuMillis,
        long memoryMi,
        int storageSizeGb,
        bool usesExistingPostgres = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cpuMillis);
        ArgumentOutOfRangeException.ThrowIfNegative(memoryMi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(storageSizeGb);
        var topology = For(mode);
        if (usesExistingPostgres && mode != SeaweedFSMode.Replicated)
            throw new ArgumentException("Existing PostgreSQL requires replicated mode.", nameof(usesExistingPostgres));
        var metadataPods = usesExistingPostgres ? 0 : topology.MetadataInstances;
        var pods = topology.Masters + topology.Volumes + topology.Filers + topology.Gateways + metadataPods;
        var pvcTotal = topology.Masters * MasterDiskGb + topology.Volumes * (long)storageSizeGb +
            metadataPods * MetadataDiskGb + (mode == SeaweedFSMode.SingleNode ? SingleFilerDiskGb : 0);
        return new SeaweedFSRequestedCapacity(checked(pods * cpuMillis), checked(pods * memoryMi), pods, pvcTotal);
    }
}
