// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Xunit;

namespace Pole.Features.RedisServices.Tests;

public sealed class RedisQuotaProfileTests
{
    [Fact]
    public void Calculate_UsesResolvedRequestsAndInstanceCount()
    {
        var subject = new RedisQuotaProfile(
            new CpuQuota(),
            new RamQuota(),
            new WorkloadInstancesQuota(),
            new RedisServiceCountQuota());

        var result = subject.Calculate(
            RedisQuotaInput.Create(requestedCpuMillis: 375, requestedMemoryMi: 640, instances: 2));

        Assert.Equal(750, result[CpuQuota.QuotaKey]);
        Assert.Equal(1280, result[RamQuota.QuotaKey]);
        Assert.Equal(2, result[WorkloadInstancesQuota.QuotaKey]);
        Assert.Equal(1, result[RedisServiceCountQuota.QuotaKey]);
    }
}
