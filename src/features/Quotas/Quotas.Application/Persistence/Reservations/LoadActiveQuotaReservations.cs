using MongoDB.Driver;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Persistence;

public delegate Task<IReadOnlyList<QuotaReservation>> LoadActiveQuotaReservations(
    string tenantId,
    DateTime nowUtc,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadActiveQuotaReservations(
    IMongoCollection<QuotaReservation> reservations)
{
    public async Task<IReadOnlyList<QuotaReservation>> InvokeAsync(
        string tenantId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        await reservations
            .Find(QuotaReservationFilters.ActiveForTenant(tenantId, nowUtc))
            .ToListAsync(cancellationToken);
}
