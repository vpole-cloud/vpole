using Pole.Features.Quotas.Contracts;

namespace Pole.Features.RedisServices.Application.Common;

public sealed record RedisQuotaInput(
    long RequestedCpuMillisPerInstance,
    long RequestedMemoryMiPerInstance,
    long Instances
)
{
    public static RedisQuotaInput Create(
        long requestedCpuMillis,
        long requestedMemoryMi,
        long instances = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedCpuMillis, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedMemoryMi, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(instances, 1);
        return new RedisQuotaInput(requestedCpuMillis, requestedMemoryMi, instances);
    }
}

public sealed class RedisServiceCountQuota : Quota<RedisQuotaInput>
{
    public static QuotaKey QuotaKey { get; } = new("RedisServiceCount");

    public override QuotaKey Key => QuotaKey;
    public override string Unit => "services";
    public override long Calculate(RedisQuotaInput input) => 1;
}

public sealed class RedisQuotaProfile(
    CpuQuota cpuQuota,
    RamQuota ramQuota,
    WorkloadInstancesQuota workloadInstancesQuota,
    RedisServiceCountQuota serviceCountQuota
) : QuotaProfile<RedisQuotaInput>
{
    public override IReadOnlyCollection<QuotaKey> Keys { get; } =
    [
        CpuQuota.QuotaKey,
        RamQuota.QuotaKey,
        WorkloadInstancesQuota.QuotaKey,
        RedisServiceCountQuota.QuotaKey,
    ];

    public override QuotaDelta Calculate(RedisQuotaInput input)
    {
        var workload = new WorkloadQuotaInput(
            input.RequestedCpuMillisPerInstance,
            input.RequestedMemoryMiPerInstance,
            input.Instances);

        return QuotaDelta.Create(
        [
            cpuQuota.Measure(workload),
            ramQuota.Measure(workload),
            workloadInstancesQuota.Measure(workload),
            serviceCountQuota.Measure(input),
        ]);
    }
}

public static class RedisQuotaResourceKey
{
    public static QuotaResourceKey Create(string redisId) => QuotaResourceKey.Create("redis-service", redisId);
}
