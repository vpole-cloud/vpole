using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Persistence;

internal sealed class MongoEnsureTenantsExist(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<Result<Unit, TenantSetNotFound>> InvokeAsync(
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
            return new Ok<Unit>(Unit.Value);

        var found = await _tenants
            .Find(TenantFilters.ByIds(normalizedTenantIds))
            .Project(tenant => tenant.Id)
            .ToListAsync(cancellationToken);
        var missing = normalizedTenantIds
            .Except(found, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return missing.Length == 0
            ? new Ok<Unit>(Unit.Value)
            : new Err<TenantSetNotFound>(new TenantSetNotFound(missing));
    }
}
