using System.Linq.Expressions;
using MongoDB.Driver;

namespace Pole.Features.Database;

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
        var typeName = typeof(TSubType).Name;

        // Handle both cases: discriminator as string or array
        return builder.Or(
            builder.Eq($"{fieldName}._t", typeName),
            builder.AnyEq($"{fieldName}._t", typeName)
        );
    }
}