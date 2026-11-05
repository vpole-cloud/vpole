using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Tenants;

internal sealed class MongoEnsureTenantsExist(IMongoCollection<Tenant> _tenants)
{
    public async Task<EnsureTenantsExistOutcome> InvokeAsync(
        IReadOnlyCollection<string> tenantIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedTenantIds = tenantIds
            .Select(tenantId => tenantId?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedTenantIds.Any(tenantId => !ObjectId.TryParse(tenantId, out _)))
        {
            throw new ArgumentException(
                "Tenant existence checks require MongoDB ObjectId values.",
                nameof(tenantIds));
        }

        if (normalizedTenantIds.Length == 0)
            return new TenantsExist();

        var found = await _tenants
            .Find(TenantFilters.ByIds(normalizedTenantIds))
            .Project(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
        var missing = normalizedTenantIds
            .Except(found, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return missing.Length == 0
            ? new TenantsExist()
            : new TenantSetNotFound(missing);
    }
}
