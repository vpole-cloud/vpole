using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Quotas.Domain;

namespace Pole.Features.Quotas.Application.Common;

public interface IQuotaDbContext : IMongoDbContext
{
    IMongoCollection<TenantQuotaPolicy> TenantQuotaPolicies { get; }
    IMongoCollection<TenantQuotaUsage> TenantQuotaUsages { get; }
    IMongoCollection<QuotaReservation> QuotaReservations { get; }
}
