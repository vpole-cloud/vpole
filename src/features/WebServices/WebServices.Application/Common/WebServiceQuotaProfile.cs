// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Primitives;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

public sealed record WebServiceDeploymentQuotaInput(
    long RequestedCpuMillisPerInstance,
    long RequestedMemoryMiPerInstance,
    long Instances
)
{
    public static WebServiceDeploymentQuotaInput Create(
        long requestedCpuMillisPerInstance,
        long requestedMemoryMiPerInstance,
        long instances)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedCpuMillisPerInstance, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedMemoryMiPerInstance, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(instances, 1);

        return new WebServiceDeploymentQuotaInput(
            requestedCpuMillisPerInstance,
            requestedMemoryMiPerInstance,
            instances);
    }
}

public sealed record WebServiceQuotaInput(
    ImmutableList<WebServiceDeploymentQuotaInput> Deployments
)
{
    public static WebServiceQuotaInput Create(
        long requestedCpuMillisPerInstance,
        long requestedMemoryMiPerInstance,
        WebServiceSpec spec,
        int fixedReplicas = 1)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Create(
        [
            WebServiceDeploymentQuotaInput.Create(
                requestedCpuMillisPerInstance,
                requestedMemoryMiPerInstance,
                spec.Autoscaling?.MaxReplicas ?? fixedReplicas),
        ]);
    }

    public static WebServiceQuotaInput Create(IEnumerable<WebServiceDeploymentQuotaInput> deployments)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        var normalized = deployments.Select(deployment => WebServiceDeploymentQuotaInput.Create(
                deployment.RequestedCpuMillisPerInstance,
                deployment.RequestedMemoryMiPerInstance,
                deployment.Instances))
            .ToImmutableList();
        if (normalized.Count == 0)
            throw new ArgumentException(
                "At least one web service deployment is required for quota calculation.",
                nameof(deployments));

        return new WebServiceQuotaInput(normalized);
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
    WebServiceCountQuota serviceCountQuota
) : QuotaProfile<WebServiceQuotaInput>
{
    public override IReadOnlyCollection<QuotaKey> Keys { get; } =
    [
        CpuQuota.QuotaKey,
        RamQuota.QuotaKey,
        WorkloadInstancesQuota.QuotaKey,
        WebServiceCountQuota.QuotaKey,
    ];

    public override QuotaDelta Calculate(WebServiceQuotaInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        long cpuMillis = 0;
        long memoryMi = 0;
        long instances = 0;
        foreach (var deployment in input.Deployments)
        {
            var workload = new WorkloadQuotaInput(
                deployment.RequestedCpuMillisPerInstance,
                deployment.RequestedMemoryMiPerInstance,
                deployment.Instances);
            cpuMillis = checked(cpuMillis + cpuQuota.Calculate(workload));
            memoryMi = checked(memoryMi + ramQuota.Calculate(workload));
            instances = checked(instances + workloadInstancesQuota.Calculate(workload));
        }

        return QuotaDelta.Create(
        [
            new QuotaValue(cpuQuota.Key, cpuMillis),
            new QuotaValue(ramQuota.Key, memoryMi),
            new QuotaValue(workloadInstancesQuota.Key, instances),
            serviceCountQuota.Measure(input),
        ]);
    }
}

public static class WebServiceQuotaResourceKey
{
    public static QuotaResourceKey Create(string webServiceId) => QuotaResourceKey.Create("web-service", webServiceId);
}
