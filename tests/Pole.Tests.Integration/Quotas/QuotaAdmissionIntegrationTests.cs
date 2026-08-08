using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using MongoDB.Bson;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

public sealed class QuotaAdmissionIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task RedisCreate_AdmitsFirstResource_AndRejectsQuotaOverflow()
    {
        await SeedPolicyAsync(TestData.TenantId, [new(RedisServiceCountQuota.QuotaKey, 1)]);
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        var first = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-a"));
        Assert.True(first.TryGetValue(out _, out _));

        var second = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-b"));
        Assert.False(second.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);

        Assert.Equal(1L, await DbContext.Database.RedisServices().CountDocumentsAsync(redis => redis.TenantId == TestData.TenantId));
        var usage = await GetUsageAsync(TestData.TenantId);
        Assert.Equal(1, QuotaValues.GetValue(usage.Values, RedisServiceCountQuota.QuotaKey));
        var reservations = await DbContext.QuotaReservations
            .Find(reservation => reservation.TenantId == TestData.TenantId)
            .ToListAsync();
        Assert.DoesNotContain(reservations, reservation => reservation.State is ActiveQuotaReservationState);
    }

    [Fact]
    public async Task PostgresCreate_ChargesCpuMemoryAndClusterQuota()
    {
        await SeedPolicyAsync(
            TestData.TenantId,
            [
                new(CpuQuota.QuotaKey, 1000),
                new(RamQuota.QuotaKey, 256),
                new(WorkloadInstancesQuota.QuotaKey, 1),
                new(PostgresClusterCountQuota.QuotaKey, 1)
            ]);
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        var first = await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id, "postgres-a"));
        Assert.True(first.TryGetValue(out _, out _));

        var second = await Mediator.Send(TestData.CreatePostgresClusterRequest(resourceGroup.Id, "postgres-b"));
        Assert.False(second.TryGetValue(out _, out var failure));
        Assert.IsType<ConflictFailure>(failure);

        var usage = await GetUsageAsync(TestData.TenantId);
        Assert.Equal(250, QuotaValues.GetValue(usage.Values, CpuQuota.QuotaKey));
        Assert.Equal(256, QuotaValues.GetValue(usage.Values, RamQuota.QuotaKey));
        Assert.Equal(1, QuotaValues.GetValue(usage.Values, WorkloadInstancesQuota.QuotaKey));
        Assert.Equal(1, QuotaValues.GetValue(usage.Values, PostgresClusterCountQuota.QuotaKey));
    }

    [Fact]
    public async Task RedisUpdateAndDelete_ApplyUsageDeltaAndReleaseCommittedUsage()
    {
        await SeedPolicyAsync(
            TestData.TenantId,
            [
                new(CpuQuota.QuotaKey, 2000),
                new(RamQuota.QuotaKey, 512),
                new(WorkloadInstancesQuota.QuotaKey, 1),
                new(RedisServiceCountQuota.QuotaKey, 1)
            ]);
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var create = await Mediator.Send(TestData.CreateRedisServiceRequest(resourceGroup.Id));
        Assert.True(create.TryGetValue(out var created, out _));

        var replacementComputeId = ObjectId.GenerateNewId().ToString();
        await Fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(TestData.SeedComputeProfile(
                replacementComputeId,
                requestedCpuMillis: 2000,
                requestedMemoryMi: 512,
                limitCpuMillis: 4000,
                limitMemoryMi: 1024,
                isDefault: false));

        var update = await Mediator.Send(new UpdateRedisServiceRequest(
            created.RedisId,
            new UpdateRedisServiceRequestBody(
                new RedisSpecDto(
                    new StorageDto(5, TestData.DefaultStorageProfileId)),
                [],
                replacementComputeId),
            TestData.TenantId));
        Assert.True(update.TryGetValue(out _, out _));

        var updatedUsage = await GetUsageAsync(TestData.TenantId);
        Assert.Equal(2000, QuotaValues.GetValue(updatedUsage.Values, CpuQuota.QuotaKey));
        Assert.Equal(512, QuotaValues.GetValue(updatedUsage.Values, RamQuota.QuotaKey));
        Assert.Equal(1, QuotaValues.GetValue(updatedUsage.Values, WorkloadInstancesQuota.QuotaKey));
        Assert.Equal(1, QuotaValues.GetValue(updatedUsage.Values, RedisServiceCountQuota.QuotaKey));

        var delete = await Mediator.Send(new DeleteRedisServiceRequest(created.RedisId, TestData.TenantId));
        Assert.True(delete.TryGetValue(out _, out _));

        var releasedUsage = await GetUsageAsync(TestData.TenantId);
        Assert.Equal(0, QuotaValues.GetValue(releasedUsage.Values, CpuQuota.QuotaKey));
        Assert.Equal(0, QuotaValues.GetValue(releasedUsage.Values, RamQuota.QuotaKey));
        Assert.Equal(0, QuotaValues.GetValue(releasedUsage.Values, WorkloadInstancesQuota.QuotaKey));
        Assert.Equal(0, QuotaValues.GetValue(releasedUsage.Values, RedisServiceCountQuota.QuotaKey));
    }

    [Fact]
    public async Task ConcurrentReservations_CannotOverCommitTenantQuota()
    {
        await SeedPolicyAsync(TestData.TenantId, [new(RedisServiceCountQuota.QuotaKey, 1)]);

        using var firstScope = Services.CreateScope();
        using var secondScope = Services.CreateScope();
        var firstAdmission = firstScope.ServiceProvider.GetRequiredService<IQuotaAdmissionService>();
        var secondAdmission = secondScope.ServiceProvider.GetRequiredService<IQuotaAdmissionService>();
        var requested = QuotaDelta.From(RedisServiceCountQuota.QuotaKey, 1);

        var outcomes = await Task.WhenAll(
            firstAdmission.ReserveAsync(
                TestData.TenantId,
                requested,
                QuotaResourceKey.Create("test", "race-1")),
            secondAdmission.ReserveAsync(
                TestData.TenantId,
                requested,
                QuotaResourceKey.Create("test", "race-2")));

        var successful = outcomes
            .Select(outcome => outcome.TryGetValue(out var lease, out _) ? lease : null)
            .Where(lease => lease is not null)
            .ToArray();
        Assert.Single(successful);
        Assert.Single(outcomes, outcome => !outcome.TryGetValue(out _, out _));

        var activeReservations = await DbContext.QuotaReservations
            .Find(reservation => reservation.TenantId == TestData.TenantId)
            .ToListAsync();
        Assert.Single(activeReservations, reservation => reservation.State is ActiveQuotaReservationState);

        var cancel = await firstAdmission.CancelAsync(successful[0]!.ReservationId);
        Assert.IsType<Success>(cancel);
    }

    [Fact]
    public async Task UsageIsTenantScoped()
    {
        const string otherTenantId = "507f1f77bcf86cd799439022";
        await SeedPolicyAsync(TestData.TenantId, [new(RedisServiceCountQuota.QuotaKey, 1)]);
        await SeedPolicyAsync(otherTenantId, [new(RedisServiceCountQuota.QuotaKey, 1)]);
        var firstGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var secondGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("other", tenantId: otherTenantId));

        var first = await Mediator.Send(TestData.CreateRedisServiceRequest(firstGroup.Id, "redis-a"));
        var second = await Mediator.Send(TestData.CreateRedisServiceRequest(secondGroup.Id, "redis-b", otherTenantId));

        Assert.True(first.TryGetValue(out _, out _));
        Assert.True(second.TryGetValue(out _, out _));
        Assert.Equal(1, QuotaValues.GetValue((await GetUsageAsync(TestData.TenantId)).Values, RedisServiceCountQuota.QuotaKey));
        Assert.Equal(1, QuotaValues.GetValue((await GetUsageAsync(otherTenantId)).Values, RedisServiceCountQuota.QuotaKey));
    }

    [Fact]
    public async Task WebServicePreparationFailureCancelsQuotaReservation()
    {
        await SeedPolicyAsync(
            TestData.TenantId,
            [
                new(CpuQuota.QuotaKey, 10_000),
                new(RamQuota.QuotaKey, 10_000),
                new(WorkloadInstancesQuota.QuotaKey, 10),
                new(WebServiceCountQuota.QuotaKey, 10)
            ]);
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        var first = await Mediator.Send(TestData.CreateWebServiceRequest(
            resourceGroup.Id,
            "web-a",
            "duplicate.example.com"));
        var second = await Mediator.Send(TestData.CreateWebServiceRequest(
            resourceGroup.Id,
            "web-b",
            "duplicate.example.com"));

        Assert.True(first.TryGetValue(out _, out _));
        Assert.False(second.TryGetValue(out _, out _));
        var usage = await GetUsageAsync(TestData.TenantId);
        Assert.Equal(1, QuotaValues.GetValue(usage.Values, WebServiceCountQuota.QuotaKey));
        var reservations = await DbContext.QuotaReservations
            .Find(reservation => reservation.TenantId == TestData.TenantId)
            .ToListAsync();
        Assert.DoesNotContain(reservations, reservation => reservation.State is ActiveQuotaReservationState);
    }

    private async Task SeedPolicyAsync(string tenantId, IEnumerable<QuotaValue> limits)
    {
        var policy = QuotaDomainFactory.CreatePolicy(tenantId, QuotaDelta.Create(limits), DateTime.UtcNow);
        await DbContext.TenantQuotaPolicies.InsertOneAsync(policy);
    }

    private async Task<TenantQuotaUsage> GetUsageAsync(string tenantId) =>
        await DbContext.TenantQuotaUsages.Find(usage => usage.TenantId == tenantId).SingleAsync();
}
