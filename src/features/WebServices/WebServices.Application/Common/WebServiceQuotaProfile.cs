using Pole.Features.Quotas.Contracts;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

public sealed class WebServiceCountQuota : Quota<WebServiceSpec>
{
    public static QuotaKey QuotaKey { get; } = new("WebServiceCount");

    public override QuotaKey Key => QuotaKey;
    public override string Unit => "services";
    public override long Calculate(WebServiceSpec input) => 1;
}

public sealed class WebServiceQuotaProfile(
    CpuQuota cpuQuota,
    RamQuota ramQuota,
    WorkloadInstancesQuota workloadInstancesQuota,
    WebServiceCountQuota serviceCountQuota) : QuotaProfile<WebServiceSpec>
{
    public override IReadOnlyCollection<QuotaKey> Keys { get; } =
    [
        CpuQuota.QuotaKey,
        RamQuota.QuotaKey,
        WorkloadInstancesQuota.QuotaKey,
        WebServiceCountQuota.QuotaKey
    ];

    public override QuotaDelta Calculate(WebServiceSpec spec)
    {
        var workload = new WorkloadQuotaInput(
            checked((long)spec.Resources.Requests.CpuCores * 1000),
            spec.Resources.Requests.MemoryMi,
            spec.Autoscaling?.MaxReplicas ?? spec.Replicas);

        return QuotaDelta.Create(
        [
            cpuQuota.Measure(workload),
            ramQuota.Measure(workload),
            workloadInstancesQuota.Measure(workload),
            serviceCountQuota.Measure(spec)
        ]);
    }
}

public static class WebServiceQuotaResourceKey
{
    public static QuotaResourceKey Create(string webServiceId) =>
        QuotaResourceKey.Create("web-service", webServiceId);
}
