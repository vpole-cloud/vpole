using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Quotas.Application.Mongo;
using Pole.Features.Quotas.Domain;
using Pole.Features.Quotas.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

[Collection(IntegrationTestCollection.Name)]
public sealed class QuotaActiveFilterTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ActiveFiltersExcludeExpiredFinalizedAndOtherTenantReservations()
    {
        var reservations = fixture.Database.GetCollection<QuotaReservation>("QuotaReservations");
        var now = DateTime.UtcNow;
        var tenant = ObjectId.GenerateNewId().ToString();
        var active = Create(tenant, new ActiveQuotaReservationState(now.AddMinutes(5), now.AddHours(1)));
        var expired = Create(tenant, new ActiveQuotaReservationState(now.AddMinutes(-5), now.AddHours(1)));
        var committed = Create(tenant, new CommittedQuotaReservationState(now, now.AddHours(1)));
        var cancelled = Create(tenant, new CancelledQuotaReservationState(now, now.AddHours(1)));
        var foreign = Create(ObjectId.GenerateNewId().ToString(),
            new ActiveQuotaReservationState(now.AddMinutes(5), now.AddHours(1)));
        await reservations.InsertManyAsync([active, expired, committed, cancelled, foreign]);

        var selected = await reservations.Find(QuotaReservationFilters.ActiveForTenant(tenant, now)).ToListAsync();

        Assert.Equal(active.Id, Assert.Single(selected).Id);
        Assert.Equal(1, await reservations.CountDocumentsAsync(QuotaReservationFilters.Active(active.Id, now)));
        Assert.Equal(0, await reservations.CountDocumentsAsync(QuotaReservationFilters.Active(expired.Id, now)));
        Assert.Equal(0, await reservations.CountDocumentsAsync(QuotaReservationFilters.Active(committed.Id)));
        Assert.Equal(0, await reservations.CountDocumentsAsync(QuotaReservationFilters.Active(cancelled.Id)));
    }

    private static QuotaReservation Create(string tenantId, QuotaReservationState state) =>
        new(ObjectId.GenerateNewId().ToString(), tenantId, [],
            QuotaResourceKey.Create("test", Guid.NewGuid().ToString("N")),
            DateTime.UtcNow, state);
}
