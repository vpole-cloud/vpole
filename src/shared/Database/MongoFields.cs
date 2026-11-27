// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Linq.Expressions;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Pole.Database;

/// <summary>Resolves aggregation field paths using the concrete document's BSON mapping.</summary>
public static class MongoFields
{
    public static string Field<TDocument, TValue>(Expression<Func<TDocument, TValue>> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var registry = BsonSerializer.SerializerRegistry;
        return new ExpressionFieldDefinition<TDocument, TValue>(field)
            .Render(new RenderArgs<TDocument>(registry.GetSerializer<TDocument>(), registry))
            .FieldName;
    }

    public static string Reference<TDocument, TValue>(Expression<Func<TDocument, TValue>> field) => "$" + Field(field);
}
