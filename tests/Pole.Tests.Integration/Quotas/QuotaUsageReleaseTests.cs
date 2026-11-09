using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.Features.Quotas.Primitives;
using Pole.Features.RedisServices.Application.Common;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

[Collection(IntegrationTestCollection.Global2)]
public sealed class QuotaUsageReleaseTests(MongoReplicaSetFixture fixture) : IsolatedMediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Joined_release_rolls_back_with_owner_transaction_and_does_not_touch_other_tenant()
    {
        var usages = Services.GetRequiredService<IMongoCollection<TenantQuotaUsage>>();
        var initial = Seed(Data.TenantId);
        var other = Seed(MongoDB.Bson.ObjectId.GenerateNewId().ToString());
        await usages.InsertManyAsync([initial, other]);
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await Services.GetRequiredService<IQuotaUsageRelease>().ReleaseAsync(
            new ReleaseQuotaUsageRequest<RedisQuotaInput>(initial.TenantId, RedisQuotaInput.Create(100, 128)),
            session, default);
        await session.AbortTransactionAsync();

        var restored = await usages.Find(item => item.TenantId == initial.TenantId).SingleAsync();
        Assert.Equal(initial.Values, restored.Values);
        Assert.Equal(initial.AdmissionRevision, restored.AdmissionRevision);

        session.StartTransaction();
        await Services.GetRequiredService<IQuotaUsageRelease>().ReleaseAsync(
            new ReleaseQuotaUsageRequest<RedisQuotaInput>(initial.TenantId, RedisQuotaInput.Create(100, 128)),
            session, default);
        await session.CommitTransactionAsync();
        var released = await usages.Find(item => item.TenantId == initial.TenantId).SingleAsync();
        Assert.Equal(900, Value(released, CpuQuota.QuotaKey));
        Assert.Equal(1920, Value(released, RamQuota.QuotaKey));
        Assert.Equal(9, Value(released, RedisServiceCountQuota.QuotaKey));
        Assert.Equal(other.Values, (await usages.Find(item => item.TenantId == other.TenantId).SingleAsync()).Values);
    }

    [Fact]
    public async Task Concurrent_joined_releases_serialize_usage_fence_without_lost_updates()
    {
        var usages = Services.GetRequiredService<IMongoCollection<TenantQuotaUsage>>();
        await usages.InsertOneAsync(Seed(Data.TenantId));
        async Task Release()
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync<bool>(
                async (tx, ct) =>
                {
                    await scope.ServiceProvider.GetRequiredService<IQuotaUsageRelease>().ReleaseAsync(
                        new ReleaseQuotaUsageRequest<RedisQuotaInput>(Data.TenantId,
                            RedisQuotaInput.Create(100, 128)), tx.Session, ct);
                    return tx.Commit(true);
                }, default);
        }

        await Task.WhenAll(Release(), Release());
        var current = await usages.Find(item => item.TenantId == Data.TenantId).SingleAsync();
        Assert.Equal(800, Value(current, CpuQuota.QuotaKey));
        Assert.Equal(1792, Value(current, RamQuota.QuotaKey));
        Assert.Equal(8, Value(current, RedisServiceCountQuota.QuotaKey));
        Assert.Equal(2, current.AdmissionRevision);
    }

    private static TenantQuotaUsage Seed(string tenant) => QuotaDomainFactory.CreateUsage(tenant, DateTime.UtcNow) with
    {
        Values = ImmutableList.Create(new QuotaValue(CpuQuota.QuotaKey, 1000), new QuotaValue(RamQuota.QuotaKey, 2048),
            new QuotaValue(WorkloadInstancesQuota.QuotaKey, 10), new QuotaValue(RedisServiceCountQuota.QuotaKey, 10))
    };

    private static long Value(TenantQuotaUsage usage,
        QuotaKey key) => usage.Values.Single(item => item.Key == key).Value;
}
