using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Primitives;

namespace Pole.Features.Functions.Application.Common;

public sealed class FunctionAppCountQuota : Quota<FunctionQuotaAllocation>
{
    public static QuotaKey QuotaKey { get; } = new("FunctionAppCount");
    public override QuotaKey Key => QuotaKey;
    public override string Unit => "apps";
    public override long Calculate(FunctionQuotaAllocation input) => input.FunctionAppCount;
}

public sealed class FunctionAppQuotaProfile(
    FunctionAppCountQuota functionAppCountQuota) : QuotaProfile<FunctionQuotaAllocation>
{
    public override IReadOnlyCollection<QuotaKey> Keys { get; } =
    [
        CpuQuota.QuotaKey,
        RamQuota.QuotaKey,
        WorkloadInstancesQuota.QuotaKey,
        FunctionAppCountQuota.QuotaKey
    ];

    public override QuotaDelta Calculate(FunctionQuotaAllocation allocation) =>
        QuotaDelta.Create(
        [
            new QuotaValue(CpuQuota.QuotaKey, allocation.TotalCpuMillis),
            new QuotaValue(RamQuota.QuotaKey, allocation.TotalMemoryMi),
            new QuotaValue(WorkloadInstancesQuota.QuotaKey, allocation.WorkloadInstances),
            functionAppCountQuota.Measure(allocation)
        ]);
}

public static class FunctionAppQuotaResourceKey
{
    public static QuotaResourceKey Create(string functionAppId) =>
        QuotaResourceKey.Create("function-app", functionAppId);
}
