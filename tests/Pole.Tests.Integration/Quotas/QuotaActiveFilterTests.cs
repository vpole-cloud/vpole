using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Quotas.Application.Reservations;
using Pole.Features.Quotas.Domain;
using Pole.Features.Quotas.Primitives;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

[Collection(IntegrationTestCollection.Name)]
public sealed class QuotaActiveFilterTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
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

        var selected = await Services.GetRequiredService<LoadActiveQuotaReservations>()(tenant, now);

        Assert.Equal(active.Id, Assert.Single(selected).Id);
    }

    private static QuotaReservation Create(string tenantId, QuotaReservationState state) =>
        new(ObjectId.GenerateNewId().ToString(), tenantId, [],
            QuotaResourceKey.Create("test", Guid.NewGuid().ToString("N")),
            DateTime.UtcNow, state);
}
