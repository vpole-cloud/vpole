// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Creation;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Features.Postgres.Tests.Integration.Infrastructure;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.Features.Quotas.Primitives;
using Xunit;

namespace Pole.Features.Postgres.Tests.Integration;

public sealed class PostgresQuotaAdmissionTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private IMongoCollection<QuotaReservation> QuotaReservations =>
        Fixture.Database.GetCollection<QuotaReservation>("QuotaReservations");

    private IMongoCollection<TenantQuotaPolicy> QuotaPolicies =>
        Fixture.Database.GetCollection<TenantQuotaPolicy>("TenantQuotaPolicies");

    private IMongoCollection<TenantQuotaUsage> QuotaUsages =>
        Fixture.Database.GetCollection<TenantQuotaUsage>("TenantQuotaUsages");

    [Fact]
    public async Task PostgresCreate_ChargesCpuMemoryAndClusterQuota()
    {
        await SeedPolicyAsync(
            PostgresTestData.TenantId,
            [
                new QuotaValue(CpuQuota.QuotaKey, 1000),
                new QuotaValue(RamQuota.QuotaKey, 256),
                new QuotaValue(WorkloadInstancesQuota.QuotaKey, 1),
                new QuotaValue(PostgresClusterCountQuota.QuotaKey, 1),
            ]);
        var resourceGroup = await DbContext.InsertResourceGroupAsync(PostgresTestData.SeedResourceGroup());

        var first = await Mediator.Send(PostgresTestData.CreatePostgresClusterRequest(resourceGroup.Id, "postgres-a"));
        _ = first.AssertCreated();

        var second = await Mediator.Send(PostgresTestData.CreatePostgresClusterRequest(resourceGroup.Id, "postgres-b"));
        var outcome = second.AssertSuccess();
        var error = Assert.IsType<CreatePostgresClusterOutcome.CreatePostgresClusterRejected>(outcome).Item;
        var quota = Assert.IsType<CreatePostgresClusterError.CreateQuota>(error).Item;
        Assert.IsType<QuotaExecutionError>(quota);

        var usage = await GetUsageAsync(PostgresTestData.TenantId);
        Assert.Equal(250, QuotaValues.GetValue(usage.Values, CpuQuota.QuotaKey));
        Assert.Equal(256, QuotaValues.GetValue(usage.Values, RamQuota.QuotaKey));
        Assert.Equal(1, QuotaValues.GetValue(usage.Values, WorkloadInstancesQuota.QuotaKey));
        Assert.Equal(1, QuotaValues.GetValue(usage.Values, PostgresClusterCountQuota.QuotaKey));
    }

    private async Task SeedPolicyAsync(string tenantId, IEnumerable<QuotaValue> limits)
    {
        var policy = QuotaDomainFactory.CreatePolicy(tenantId, QuotaDelta.Create(limits), DateTime.UtcNow);
        await QuotaPolicies.InsertOneAsync(policy);
    }

    private async Task<TenantQuotaUsage> GetUsageAsync(string tenantId) =>
        await QuotaUsages.Find(usage => usage.TenantId == tenantId).SingleAsync();
}
