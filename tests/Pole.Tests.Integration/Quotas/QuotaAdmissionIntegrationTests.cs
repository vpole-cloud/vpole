using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using MongoDB.Bson;
using Pole.Database;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Quotas.Application.Persistence;
using Pole.Features.Quotas.Application.Persistence.Reservations;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

public sealed class QuotaAdmissionIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{




    [Fact]
    public async Task ConcurrentReservations_CannotOverCommitTenantQuota()
    {
        await SeedPolicyAsync(TestData.TenantId, [new(CpuQuota.QuotaKey, 1)]);

        using var firstScope = Services.CreateScope();
        using var secondScope = Services.CreateScope();
        var firstReservation = firstScope.ServiceProvider
            .GetRequiredService<ReserveQuotaReservation>();
        var secondReservation = secondScope.ServiceProvider
            .GetRequiredService<ReserveQuotaReservation>();
        var requested = QuotaDelta.From(CpuQuota.QuotaKey, 1);

        var outcomes = await Task.WhenAll(
            firstReservation(
                TestData.TenantId,
                requested,
                QuotaResourceKey.Create("test", "race-1")),
            secondReservation(
                TestData.TenantId,
                requested,
                QuotaResourceKey.Create("test", "race-2")));

        var successful = outcomes
            .Select(outcome => outcome.TryGetValue(out var lease, out _) ? lease : null)
            .Where(lease => lease is not null)
            .ToArray();
        Assert.Single(successful);
        Assert.Single(outcomes, outcome => !outcome.TryGetValue(out _, out _));

        var activeReservations = await QuotaReservations
            .Find(reservation => reservation.TenantId == TestData.TenantId)
            .ToListAsync();
        Assert.Single(activeReservations, reservation => reservation.State is ActiveQuotaReservationState);

        var cancel = Services.GetRequiredService<CancelQuotaReservation>();
        await cancel(successful[0]!.ReservationId);
    }

    [Fact]
    public async Task ReservationReturnsStructuredQuotaExceededError()
    {
        await SeedPolicyAsync(
            TestData.TenantId,
            [new(CpuQuota.QuotaKey, 1)]);
        var reserve = Services.GetRequiredService<ReserveQuotaReservation>();
        var requested = QuotaDelta.From(
            CpuQuota.QuotaKey,
            1);
        var first = await reserve(
            TestData.TenantId,
            requested,
            QuotaResourceKey.Create("test", "first"));
        Assert.True(first.TryGetValue(out var lease, out _));

        var second = await reserve(
            TestData.TenantId,
            requested,
            QuotaResourceKey.Create("test", "second"));

        Assert.False(second.TryGetValue(out _, out var error));
        var exceeded = Assert.IsType<QuotaExceeded>(error.Value);
        Assert.Equal(CpuQuota.QuotaKey, exceeded.Quota);
        Assert.Equal(1, exceeded.Limit);
        Assert.Equal(1, exceeded.Requested);
        Assert.Equal(1, exceeded.Current);

        var cancel = Services.GetRequiredService<CancelQuotaReservation>();
        await cancel(lease.ReservationId);
    }


    [Fact]
    public async Task ReservationRejectsStructuralInvalidInput()
    {
        var reserve = Services.GetRequiredService<ReserveQuotaReservation>();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => reserve(
            string.Empty,
            QuotaDelta.From(CpuQuota.QuotaKey, 1),
            QuotaResourceKey.Create("test", "invalid")));

        Assert.Equal("tenantId", exception.ParamName);
    }


    private async Task SeedPolicyAsync(string tenantId, IEnumerable<QuotaValue> limits)
    {
        var policy = QuotaDomainFactory.CreatePolicy(tenantId, QuotaDelta.Create(limits), DateTime.UtcNow);
        await QuotaPolicies.InsertOneAsync(policy);
    }

    private async Task<TenantQuotaUsage> GetUsageAsync(string tenantId) =>
        await QuotaUsages.Find(usage => usage.TenantId == tenantId).SingleAsync();

    private IMongoCollection<QuotaReservation> QuotaReservations =>
        Fixture.Database.GetCollection<QuotaReservation>("QuotaReservations");

    private IMongoCollection<TenantQuotaPolicy> QuotaPolicies =>
        Fixture.Database.GetCollection<TenantQuotaPolicy>("TenantQuotaPolicies");

    private IMongoCollection<TenantQuotaUsage> QuotaUsages =>
        Fixture.Database.GetCollection<TenantQuotaUsage>("TenantQuotaUsages");
}

public sealed record TestMutationAccepted;

public sealed record TestMutationRejected;

public union TestQuotaOutcome(
    TestMutationAccepted,
    TestMutationRejected,
    QuotaExecutionError);
