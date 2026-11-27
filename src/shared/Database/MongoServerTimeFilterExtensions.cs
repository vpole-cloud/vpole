// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Architecture;

namespace Pole.Database;

public static class MongoServerTimeFilterExtensions
{
    public static FilterDefinition<TDocument> AfterServerNow<TDocument>(
        this FilterDefinitionBuilder<TDocument> builder,
        Expression<Func<TDocument, DateTime>> field) => new ServerTimeFilter<TDocument>(field, "$gt");

    public static FilterDefinition<TDocument> AtOrBeforeServerNow<TDocument>(
        this FilterDefinitionBuilder<TDocument> builder,
        Expression<Func<TDocument, DateTime>> field) => new ServerTimeFilter<TDocument>(field, "$lte");

    // MongoDB evaluates $$NOW; resolving the field at render time preserves the collection's mapping.
    [ViolateArchitecture(
        ArchitectureRules.RawBson,
        "The driver FilterDefinition.Render contract returns BSON; $$NOW expressions must resolve " +
        "mapped fields at render time.")]
    private sealed class ServerTimeFilter<TDocument>(
        Expression<Func<TDocument, DateTime>> field,
        string comparison
    ) : FilterDefinition<TDocument>
    {
        public override BsonDocument Render(RenderArgs<TDocument> args)
        {
            var path = new ExpressionFieldDefinition<TDocument, DateTime>(field).Render(args).FieldName;
            return new BsonDocument("$expr", new BsonDocument(comparison, new BsonArray { "$" + path, "$$NOW" }));
        }
    }
}
