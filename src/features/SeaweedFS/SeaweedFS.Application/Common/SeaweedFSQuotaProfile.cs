using Pole.Features.Quotas.Contracts;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Common;

public sealed record SeaweedFSQuotaInput(
    long CpuMillis,
    long MemoryMi,
    long Instances,
    long StorageGb
)
{
    public static SeaweedFSQuotaInput Create(
        SeaweedFSSpec spec,
        long cpuMillis,
        long memoryMi)
    {
        var totals = SeaweedFSTopology.Calculate(
            spec.Mode,
            cpuMillis,
            memoryMi,
            spec.Storage.SizeGb,
            spec.Metadata is ExistingPostgresSeaweedFSMetadata);
        return new(totals.CpuMillis, totals.MemoryMi, totals.WorkloadInstances, totals.StorageGb);
    }
}

public sealed class SeaweedFSServiceCountQuota : Quota<SeaweedFSQuotaInput>
{
    public static QuotaKey QuotaKey { get; } = new("SeaweedFSServiceCount");
    public override QuotaKey Key => QuotaKey;
    public override string Unit => "services";
    public override long Calculate(SeaweedFSQuotaInput input) => 1;
}

public sealed class SeaweedFSStorageGbQuota : Quota<SeaweedFSQuotaInput>
{
    public static QuotaKey QuotaKey { get; } = new("SeaweedFSStorageGb");
    public override QuotaKey Key => QuotaKey;
    public override string Unit => "GiB";
    public override long Calculate(SeaweedFSQuotaInput input) => input.StorageGb;
}

public sealed class SeaweedFSQuotaProfile(
    SeaweedFSServiceCountQuota count,
    SeaweedFSStorageGbQuota storage
) : QuotaProfile<SeaweedFSQuotaInput>
{
    public override IReadOnlyCollection<QuotaKey> Keys { get; } =
    [
        CpuQuota.QuotaKey, RamQuota.QuotaKey, WorkloadInstancesQuota.QuotaKey, SeaweedFSServiceCountQuota.QuotaKey,
        SeaweedFSStorageGbQuota.QuotaKey
    ];

    public override QuotaDelta Calculate(SeaweedFSQuotaInput input) => QuotaDelta.Create(
    [
        new(CpuQuota.QuotaKey, input.CpuMillis), new(RamQuota.QuotaKey, input.MemoryMi),
        new(WorkloadInstancesQuota.QuotaKey, input.Instances), count.Measure(input), storage.Measure(input)
    ]);
}

public static class SeaweedFSQuotaResourceKey
{
    public static QuotaResourceKey Create(string id) => QuotaResourceKey.Create("seaweedfs", id);
}
