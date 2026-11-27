// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.Quotas.Application.Reservations;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

[Collection(IntegrationTestCollection.Tenant4)]
public sealed class QuotaAdmissionIntegrationTests(
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
    public async Task ConcurrentReservations_CannotOverCommitTenantQuota()
    {
        await SeedPolicyAsync(Data.TenantId, [new QuotaValue(CpuQuota.QuotaKey, 1)]);

        using var firstScope = Services.CreateScope();
        using var secondScope = Services.CreateScope();
        var firstReservation = firstScope.ServiceProvider.GetRequiredService<ReserveQuotaReservation>();
        var secondReservation = secondScope.ServiceProvider.GetRequiredService<ReserveQuotaReservation>();
        var requested = QuotaDelta.From(CpuQuota.QuotaKey, 1);

        var outcomes = await Task.WhenAll(
            firstReservation(Data.TenantId, requested, QuotaResourceKey.Create("test", "race-1")),
            secondReservation(Data.TenantId, requested, QuotaResourceKey.Create("test", "race-2")));

        var successful = outcomes.Select(outcome => outcome.Value).OfType<QuotaReservationLease>().ToArray();
        Assert.Single(successful);
        Assert.Single(outcomes, outcome => outcome is QuotaExceeded);

        var activeReservations = await QuotaReservations.Find(reservation => reservation.TenantId == Data.TenantId)
            .ToListAsync();
        Assert.Single(activeReservations, reservation => reservation.State is ActiveQuotaReservationState);

        var cancel = Services.GetRequiredService<CancelQuotaReservation>();
        await cancel(successful[0].ReservationId);
    }

    [Fact]
    public async Task ReservationReturnsStructuredQuotaExceededError()
    {
        await SeedPolicyAsync(Data.TenantId, [new QuotaValue(CpuQuota.QuotaKey, 1)]);
        var reserve = Services.GetRequiredService<ReserveQuotaReservation>();
        var requested = QuotaDelta.From(CpuQuota.QuotaKey, 1);
        var first = await reserve(Data.TenantId, requested, QuotaResourceKey.Create("test", "first"));
        var lease = Assert.IsType<QuotaReservationLease>(first.Value);

        var second = await reserve(Data.TenantId, requested, QuotaResourceKey.Create("test", "second"));

        var exceeded = Assert.IsType<QuotaExceeded>(second.Value);
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
            "",
            QuotaDelta.From(CpuQuota.QuotaKey, 1),
            QuotaResourceKey.Create("test", "invalid")));

        Assert.Equal("tenantId", exception.ParamName);
    }


    [Fact]
    public async Task CommitReservation_ChargesUsageOnceAcrossRepeatedCommits()
    {
        var reserve = Services.GetRequiredService<ReserveQuotaReservation>();
        var commit = Services.GetRequiredService<CommitQuotaReservation>();
        var reserved = await reserve(
            Data.TenantId,
            QuotaDelta.From(CpuQuota.QuotaKey, 2),
            QuotaResourceKey.Create("test", "idempotent-commit"));
        var lease = Assert.IsType<QuotaReservationLease>(reserved.Value);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var session = await Fixture.Client.StartSessionAsync();
            session.StartTransaction();
            var result = await commit(lease.ReservationId, session);
            Assert.IsType<QuotaReservationCommitted>(result.Value);
            await session.CommitTransactionAsync();
        }

        var usage = await GetUsageAsync(Data.TenantId);
        Assert.Equal(2, QuotaValues.GetValue(usage.Values, CpuQuota.QuotaKey));
        var persisted = await QuotaReservations.Find(item => item.Id == lease.ReservationId).SingleAsync();
        Assert.IsType<CommittedQuotaReservationState>(persisted.State);
    }

    [Theory]
    [InlineData("missing", typeof(QuotaReservationNotFound))]
    [InlineData("cancelled", typeof(QuotaReservationCancelled))]
    [InlineData("expired", typeof(QuotaReservationExpired))]
    public async Task CommitReservation_RejectsUnavailableReservationWithoutUsage(string state, Type expectedError)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcDateTime;
        var reservation = QuotaDomainFactory.CreateReservation(
            Data.TenantId,
            QuotaDelta.From(CpuQuota.QuotaKey, 2),
            QuotaResourceKey.Create("test", "rejected-commit"),
            now.AddHours(-2),
            TimeSpan.FromHours(1),
            TimeSpan.FromDays(1));
        if (state == "cancelled")
            reservation = reservation with { State = QuotaDomainFactory.CancelReservation(now, TimeSpan.FromDays(1)) };
        if (state != "missing")
            await QuotaReservations.InsertOneAsync(reservation);

        var commit = Services.GetRequiredService<CommitQuotaReservation>();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await commit(reservation.Id, session);
        Assert.IsType(expectedError, result.Value);
        Assert.False(await QuotaUsages.Find(session, usage => usage.TenantId == Data.TenantId).AnyAsync());
        await session.AbortTransactionAsync();

        Assert.False(await QuotaUsages.Find(usage => usage.TenantId == Data.TenantId).AnyAsync());
        if (state != "missing")
        {
            var persisted = await QuotaReservations.Find(item => item.Id == reservation.Id).SingleAsync();
            Assert.Equal(reservation.State, persisted.State);
        }
    }

    private async Task SeedPolicyAsync(string tenantId, IEnumerable<QuotaValue> limits)
    {
        var policy = QuotaDomainFactory.CreatePolicy(tenantId, QuotaDelta.Create(limits), DateTime.UtcNow);
        await QuotaPolicies.InsertOneAsync(policy);
    }

    private async Task<TenantQuotaUsage> GetUsageAsync(string tenantId) =>
        await QuotaUsages.Find(usage => usage.TenantId == tenantId).SingleAsync();
}

public sealed record TestMutationAccepted;

public sealed record TestMutationRejected;

public union TestQuotaOutcome(
    TestMutationAccepted,
    TestMutationRejected,
    QuotaExecutionError);
