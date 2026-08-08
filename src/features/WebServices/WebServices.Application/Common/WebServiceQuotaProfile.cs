using Pole.Features.Quotas.Contracts;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

public sealed record WebServiceQuotaInput(
    long RequestedCpuMillisPerInstance,
    long RequestedMemoryMiPerInstance,
    long Instances)
{
    public static WebServiceQuotaInput Create(
        long requestedCpuMillisPerInstance,
        long requestedMemoryMiPerInstance,
        WebServiceSpec spec)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            requestedCpuMillisPerInstance,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            requestedMemoryMiPerInstance,
            1);
        ArgumentNullException.ThrowIfNull(spec);
        var instances = spec.Autoscaling?.MaxReplicas ?? spec.Replicas;
        ArgumentOutOfRangeException.ThrowIfLessThan(instances, 1);

        return new WebServiceQuotaInput(
            requestedCpuMillisPerInstance,
            requestedMemoryMiPerInstance,
            instances);
    }
}

public sealed class WebServiceCountQuota : Quota<WebServiceQuotaInput>
{
    public static QuotaKey QuotaKey { get; } = new("WebServiceCount");

    public override QuotaKey Key => QuotaKey;
    public override string Unit => "services";
    public override long Calculate(WebServiceQuotaInput input) => 1;
}

public sealed class WebServiceQuotaProfile(
    CpuQuota cpuQuota,
    RamQuota ramQuota,
    WorkloadInstancesQuota workloadInstancesQuota,
    WebServiceCountQuota serviceCountQuota) : QuotaProfile<WebServiceQuotaInput>
{
    public override IReadOnlyCollection<QuotaKey> Keys { get; } =
    [
        CpuQuota.QuotaKey,
        RamQuota.QuotaKey,
        WorkloadInstancesQuota.QuotaKey,
        WebServiceCountQuota.QuotaKey
    ];

    public override QuotaDelta Calculate(WebServiceQuotaInput input)
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
            serviceCountQuota.Measure(input)
        ]);
    }
}

public static class WebServiceQuotaResourceKey
{
    public static QuotaResourceKey Create(string webServiceId) =>
        QuotaResourceKey.Create("web-service", webServiceId);
}
