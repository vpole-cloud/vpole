using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Common;

public sealed class RedisServiceCountQuota : Quota<RedisSpec>
{
    public static QuotaKey QuotaKey { get; } = new("RedisServiceCount");

    public override QuotaKey Key => QuotaKey;
    public override string Unit => "services";
    public override long Calculate(RedisSpec input) => 1;
}

public sealed class RedisQuotaProfile(
    CpuQuota cpuQuota,
    RamQuota ramQuota,
    WorkloadInstancesQuota workloadInstancesQuota,
    RedisServiceCountQuota serviceCountQuota) : QuotaProfile<RedisSpec>
{
    public override IReadOnlyCollection<QuotaKey> Keys { get; } =
    [
        CpuQuota.QuotaKey,
        RamQuota.QuotaKey,
        WorkloadInstancesQuota.QuotaKey,
        RedisServiceCountQuota.QuotaKey
    ];

    public override QuotaDelta Calculate(RedisSpec spec)
    {
        var workload = new WorkloadQuotaInput(
            checked((long)spec.Resources.Requests.CpuCores * 1000),
            spec.Resources.Requests.MemoryMi,
            1);

        return QuotaDelta.Create(
        [
            cpuQuota.Measure(workload),
            ramQuota.Measure(workload),
            workloadInstancesQuota.Measure(workload),
            serviceCountQuota.Measure(spec)
        ]);
    }
}

public static class RedisQuotaResourceKey
{
    public static QuotaResourceKey Create(string redisId) =>
        QuotaResourceKey.Create("redis-service", redisId);
}
