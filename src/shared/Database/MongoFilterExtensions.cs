using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;

namespace Pole.Database;

public static class MongoFilterExtensions
{
    public static FilterDefinition<TDocument> ForTenant<TDocument>(
        this FilterDefinitionBuilder<TDocument> builder,
        TenantContext tenantContext)
        where TDocument : ITenantOwned
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tenantContext);

        return builder.Eq(document => document.TenantId, tenantContext.TenantId);
    }

    public static FilterDefinition<TDocument> ForTenant<TDocument>(
        this FilterDefinitionBuilder<TDocument> builder,
        string tenantId)
        where TDocument : ITenantOwned
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return builder.Eq(document => document.TenantId, tenantId);
    }
}
