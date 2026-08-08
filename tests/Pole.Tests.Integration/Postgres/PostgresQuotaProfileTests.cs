using Pole.Features.Postgres.Application.Common;
using Pole.Features.Quotas.Contracts;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresQuotaProfileTests
{
    [Fact]
    public void Calculate_UsesResolvedRequestsExactlyOncePerInstance()
    {
        var subject = new PostgresQuotaProfile(
            new CpuQuota(),
            new RamQuota(),
            new WorkloadInstancesQuota(),
            new PostgresClusterCountQuota());

        var result = subject.Calculate(
            new PostgresQuotaInput(
                375,
                640,
                2));

        Assert.Equal(750, result[CpuQuota.QuotaKey]);
        Assert.Equal(1280, result[RamQuota.QuotaKey]);
        Assert.Equal(2, result[WorkloadInstancesQuota.QuotaKey]);
        Assert.Equal(1, result[PostgresClusterCountQuota.QuotaKey]);
    }
}
