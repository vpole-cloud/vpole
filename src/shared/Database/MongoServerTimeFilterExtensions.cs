using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Database;

public static class MongoServerTimeFilterExtensions
{
    public static FilterDefinition<TDocument> AfterServerNow<TDocument>(
        this FilterDefinitionBuilder<TDocument> builder,
        Expression<Func<TDocument, DateTime>> field) =>
        new ServerTimeFilter<TDocument>(field, "$gt");

    public static FilterDefinition<TDocument> AtOrBeforeServerNow<TDocument>(
        this FilterDefinitionBuilder<TDocument> builder,
        Expression<Func<TDocument, DateTime>> field) =>
        new ServerTimeFilter<TDocument>(field, "$lte");

    // MongoDB evaluates $$NOW; resolving the field at render time preserves the collection's mapping.
    private sealed class ServerTimeFilter<TDocument>(
        Expression<Func<TDocument, DateTime>> field,
        string comparison) : FilterDefinition<TDocument>
    {
        public override BsonDocument Render(RenderArgs<TDocument> args)
        {
            var path = new ExpressionFieldDefinition<TDocument, DateTime>(field).Render(args).FieldName;
            return new BsonDocument("$expr", new BsonDocument(comparison,
                new BsonArray { "$" + path, "$$NOW" }));
        }
    }
}
