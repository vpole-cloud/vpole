using System.Linq.Expressions;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;

namespace Pole.Database;

public static class MongoFilterExtensions
{
    /// <summary>
    /// Builds a filter that matches documents where the property at <paramref name="field"/>
    /// is a subclass instance of <typeparamref name="TSubType"/>.
    /// Works for polymorphic records/classes stored with BsonDiscriminator.
    /// </summary>
    public static FilterDefinition<TDocument> OfType<TDocument, TSubType>(
        this FilterDefinitionBuilder<TDocument> builder,
        Expression<Func<TDocument, object>> field)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(field);

        var fieldName = ((MemberExpression)field.Body).Member.Name;
        var discriminator = BsonClassMap
            .LookupClassMap(typeof(TSubType))
            .Discriminator;

        // Handle both cases: discriminator as string or array
        return builder.Or(
            builder.Eq($"{fieldName}._t", discriminator),
            builder.AnyEq($"{fieldName}._t", discriminator)
        );
    }

    public static FilterDefinition<TDocument> ForTenant<TDocument>(
        this FilterDefinitionBuilder<TDocument> builder,
        TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tenantContext);

        return builder.Eq("TenantId", tenantContext.TenantId);
    }

    public static FilterDefinition<TDocument> ForTenant<TDocument>(
        this FilterDefinitionBuilder<TDocument> builder,
        string tenantId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return builder.Eq("TenantId", tenantId);
    }
}
