// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Quotas.Contracts;
using Pole.Features.WebServices.Application.Common;
using Xunit;

namespace Pole.Features.WebServices.Tests;

public sealed class WebServiceQuotaProfileTests
{
    [Fact]
    public void WebServiceProfileUsesAutoscalingMaximumAsWorkloadSize()
    {
        var profile = new WebServiceQuotaProfile(
            new CpuQuota(),
            new RamQuota(),
            new WorkloadInstancesQuota(),
            new WebServiceCountQuota());
        var dto = WebServiceTestData.CreateSpec() with
        {
            Autoscaling = new AutoscalingSpecDto(2, 5, new AutoscalingMetricDto("cpu", 75)),
        };
        var spec = dto.ToDomain();

        var impact = profile.Calculate(WebServiceQuotaInput.Create(250, 256, spec));

        Assert.Equal(1250, impact[CpuQuota.QuotaKey]);
        Assert.Equal(1280, impact[RamQuota.QuotaKey]);
        Assert.Equal(5, impact[WorkloadInstancesQuota.QuotaKey]);
        Assert.Equal(1, impact[WebServiceCountQuota.QuotaKey]);
    }

    [Fact]
    public void WebServiceProfileSumsDesiredCapacityAcrossDeploymentTargets()
    {
        var profile = new WebServiceQuotaProfile(
            new CpuQuota(),
            new RamQuota(),
            new WorkloadInstancesQuota(),
            new WebServiceCountQuota());
        var input = WebServiceQuotaInput.Create(
        [
            WebServiceDeploymentQuotaInput.Create(250, 256, 2),
            WebServiceDeploymentQuotaInput.Create(1000, 2048, 3),
        ]);

        var impact = profile.Calculate(input);

        Assert.Equal(3500, impact[CpuQuota.QuotaKey]);
        Assert.Equal(6656, impact[RamQuota.QuotaKey]);
        Assert.Equal(5, impact[WorkloadInstancesQuota.QuotaKey]);
        Assert.Equal(1, impact[WebServiceCountQuota.QuotaKey]);
    }
}
