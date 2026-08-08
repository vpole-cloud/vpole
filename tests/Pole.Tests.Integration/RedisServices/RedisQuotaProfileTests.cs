using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Xunit;

namespace Pole.Tests.Integration.RedisServices;

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
            RedisQuotaInput.Create(
                requestedCpuMillis: 375,
                requestedMemoryMi: 640,
                instances: 2));

        Assert.Equal(750, result[CpuQuota.QuotaKey]);
        Assert.Equal(1280, result[RamQuota.QuotaKey]);
        Assert.Equal(2, result[WorkloadInstancesQuota.QuotaKey]);
        Assert.Equal(1, result[RedisServiceCountQuota.QuotaKey]);
    }
}
