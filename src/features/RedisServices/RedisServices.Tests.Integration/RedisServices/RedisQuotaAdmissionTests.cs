// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.Features.Quotas.Primitives;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.RedisServices.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Features.RedisServices.Tests.Integration.RedisServices;

public sealed class RedisQuotaAdmissionTests(
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
    public async Task Create_AdmitsFirstResource_AndRejectsQuotaOverflow()
    {
        await SeedPolicyAsync(RedisTestData.TenantId, [new QuotaValue(RedisServiceCountQuota.QuotaKey, 1)]);
        var resourceGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());

        _ = (await Mediator.Send(RedisTestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-a"))).AssertSuccess();
        var second = await Mediator.Send(RedisTestData.CreateRedisServiceRequest(resourceGroup.Id, "redis-b"));
        Assert.IsType<QuotaExecutionError>(second.AssertSuccess<CreateRedisServiceOutcome>().Value);

        Assert.Equal(
            1L,
            await DbContext.RedisServices.CountDocumentsAsync(redis => redis.TenantId == RedisTestData.TenantId));
        Assert.Equal(
            1,
            QuotaValues.GetValue(
                (await GetUsageAsync(RedisTestData.TenantId)).Values,
                RedisServiceCountQuota.QuotaKey));
        var reservations = await QuotaReservations.Find(reservation => reservation.TenantId == RedisTestData.TenantId)
            .ToListAsync();
        Assert.DoesNotContain(reservations, reservation => reservation.State is ActiveQuotaReservationState);
    }

    [Fact]
    public async Task UpdateAndDelete_ApplyUsageDeltaAndReleaseCommittedUsage()
    {
        await SeedPolicyAsync(
            RedisTestData.TenantId,
            [
                new QuotaValue(CpuQuota.QuotaKey, 2000),
                new QuotaValue(RamQuota.QuotaKey, 512),
                new QuotaValue(WorkloadInstancesQuota.QuotaKey, 1),
                new QuotaValue(RedisServiceCountQuota.QuotaKey, 1),
            ]);
        var resourceGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());
        var created = (await Mediator.Send(RedisTestData.CreateRedisServiceRequest(resourceGroup.Id))).AssertSuccess();

        var replacementComputeId = ObjectId.GenerateNewId().ToString();
        await Fixture.Database
            .GetCollection<ComputeProfile>("ComputeProfiles")
            .InsertOneAsync(
                RedisTestData.SeedComputeProfile(
                    replacementComputeId,
                    requestedCpuMillis: 2000,
                    requestedMemoryMi: 512,
                    limitCpuMillis: 4000,
                    limitMemoryMi: 1024,
                    isDefault: false));

        _ = (await Mediator.Send(
            new UpdateRedisServiceRequest(
                created.RedisId,
                new UpdateRedisServiceRequestBody(
                    new RedisSpecDto(new StorageDto(5, RedisTestData.DefaultStorageProfileId)),
                    [],
                    replacementComputeId),
                RedisTestData.TenantId))).AssertSuccess();

        var updatedUsage = await GetUsageAsync(RedisTestData.TenantId);
        Assert.Equal(2000, QuotaValues.GetValue(updatedUsage.Values, CpuQuota.QuotaKey));
        Assert.Equal(512, QuotaValues.GetValue(updatedUsage.Values, RamQuota.QuotaKey));
        Assert.Equal(1, QuotaValues.GetValue(updatedUsage.Values, WorkloadInstancesQuota.QuotaKey));
        Assert.Equal(1, QuotaValues.GetValue(updatedUsage.Values, RedisServiceCountQuota.QuotaKey));

        _ = (await Mediator.Send(new DeleteRedisServiceRequest(created.RedisId, RedisTestData.TenantId)))
            .AssertSuccess();
        var releasedUsage = await GetUsageAsync(RedisTestData.TenantId);
        Assert.Equal(0, QuotaValues.GetValue(releasedUsage.Values, CpuQuota.QuotaKey));
        Assert.Equal(0, QuotaValues.GetValue(releasedUsage.Values, RamQuota.QuotaKey));
        Assert.Equal(0, QuotaValues.GetValue(releasedUsage.Values, WorkloadInstancesQuota.QuotaKey));
        Assert.Equal(0, QuotaValues.GetValue(releasedUsage.Values, RedisServiceCountQuota.QuotaKey));
    }

    [Fact]
    public async Task UsageIsTenantScoped()
    {
        const string otherTenantId = "507f1f77bcf86cd799439022";
        await SeedPolicyAsync(RedisTestData.TenantId, [new QuotaValue(RedisServiceCountQuota.QuotaKey, 1)]);
        await SeedPolicyAsync(otherTenantId, [new QuotaValue(RedisServiceCountQuota.QuotaKey, 1)]);
        var firstGroup = await DbContext.InsertResourceGroupAsync(RedisTestData.SeedResourceGroup());
        var secondGroup = await DbContext.InsertResourceGroupAsync(
            RedisTestData.SeedResourceGroup("other", otherTenantId));

        _ = (await Mediator.Send(RedisTestData.CreateRedisServiceRequest(firstGroup.Id, "redis-a"))).AssertSuccess();
        _ = (await Mediator.Send(RedisTestData.CreateRedisServiceRequest(secondGroup.Id, "redis-b", otherTenantId)))
            .AssertSuccess();

        Assert.Equal(
            1,
            QuotaValues.GetValue(
                (await GetUsageAsync(RedisTestData.TenantId)).Values,
                RedisServiceCountQuota.QuotaKey));
        Assert.Equal(
            1,
            QuotaValues.GetValue((await GetUsageAsync(otherTenantId)).Values, RedisServiceCountQuota.QuotaKey));
    }

    [Fact]
    public async Task TypedExecutorAbort_CancelsReservationAndDoesNotChargeUsage()
    {
        await SeedPolicyAsync(RedisTestData.TenantId, [new QuotaValue(RedisServiceCountQuota.QuotaKey, 10)]);
        var executor = Services.GetRequiredService<IQuotaAdmissionExecutor>();

        var outcome = await executor.ExecuteOutcomeAsync(
            new ExecuteQuotaAdmissionRequest<RedisQuotaInput, TestQuotaOutcome>(
                TenantId: RedisTestData.TenantId,
                ResourceKey: QuotaResourceKey.Create("test", "typed-abort"),
                Mutation: new CreateQuotaMutation<RedisQuotaInput>(new RedisQuotaInput(100, 128, 1)),
                QuotaError: error => new TestQuotaOutcome(error),
                MutationCallback: (transaction, _) => Task.FromResult(
                    transaction.Abort(new TestQuotaOutcome(new TestMutationRejected())))));

        Assert.IsType<TestMutationRejected>(outcome.Value);
        Assert.Equal(
            0,
            QuotaValues.GetValue(
                (await GetUsageAsync(RedisTestData.TenantId)).Values,
                RedisServiceCountQuota.QuotaKey));
    }

    private async Task SeedPolicyAsync(string tenantId, IEnumerable<QuotaValue> limits) =>
        await QuotaPolicies.InsertOneAsync(
            QuotaDomainFactory.CreatePolicy(tenantId, QuotaDelta.Create(limits), DateTime.UtcNow));

    private async Task<TenantQuotaUsage> GetUsageAsync(string tenantId) =>
        await QuotaUsages.Find(usage => usage.TenantId == tenantId).SingleAsync();
}

public sealed record TestMutationAccepted;

public sealed record TestMutationRejected;

public union TestQuotaOutcome(
    TestMutationAccepted,
    TestMutationRejected,
    QuotaExecutionError);
