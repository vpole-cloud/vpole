// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Pole.Architecture;
using Pole.Observation;

namespace Pole.Database.Observation;

// Raw BSON is confined to aggregation expressions that the typed builders cannot express.
[ViolateArchitecture(
    ArchitectureRules.RawBson,
    "Mongo aggregation expressions require $$NOW, $literal and serializer-resolved paths; values " +
    "must not become executable expressions.")]
internal static class MongoObservationExpressions
{
    internal static string Field<T, TValue>(Expression<Func<T, TValue>> expression) => MongoFields.Field(expression);

    internal static string Reference<T, TValue>(Expression<Func<T, TValue>> expression) =>
        MongoFields.Reference(expression);

    internal static BsonDocument Literal(BsonValue value) => new("$literal", value);

    internal static BsonDocument Increment<T>(Expression<Func<T, long>> field) =>
        new("$add", new BsonArray { Reference(field), 1L });

    internal static BsonDocument After(TimeSpan interval) => new(
        "$dateAdd",
        new BsonDocument
        {
            { "startDate", "$$NOW" }, { "unit", "second" }, { "amount", (int)interval.TotalSeconds },
        });

    internal static FilterDefinition<T> Future<T>(Expression<Func<T, DateTime>> field) => CompareNow(field, "$gt");

    internal static FilterDefinition<T> Expired<T>(Expression<Func<T, DateTime>> field) => CompareNow(field, "$lte");

    internal static FilterDefinition<T> Fresh<T>(Expression<Func<T, DateTime>> field) => new BsonDocument(
        "$expr",
        new BsonDocument(
            "$and",
            new BsonArray
            {
                new BsonDocument("$gt", new BsonArray { Reference(field), After(-ObservationPolicy.Freshness) }),
                new BsonDocument("$lte", new BsonArray { Reference(field), "$$NOW" }),
            }));

    internal static ProjectionDefinition<TDemand, MongoObservationDemandGroup> DemandSubjects<TDemand>(
        Expression<Func<TDemand, string>> subject,
        Expression<Func<TDemand, string>>? tenant) => new BsonDocument
    {
        { Field<MongoObservationDemandGroup, string>(group => group.Id), Reference(subject) },
        {
            Field<MongoObservationDemandGroup, string?>(group => group.TenantId),
            new BsonDocument("$first", tenant is null ? BsonNull.Value : Reference(tenant))
        },
    };

    // These updates contain literal $set assignments only; never splice their values as expressions.
    internal static BsonDocument LiteralAssignments<T>(UpdateDefinition<T> assignments)
    {
        var values = assignments.Render(Arguments<T>())["$set"].AsBsonDocument;
        return new BsonDocument(values.Select(element => new BsonElement(element.Name, Literal(element.Value))));
    }

    private static FilterDefinition<T> CompareNow<T>(Expression<Func<T, DateTime>> field, string comparison) =>
        new BsonDocument("$expr", new BsonDocument(comparison, new BsonArray { Reference(field), "$$NOW" }));

    private static RenderArgs<T> Arguments<T>() => new(
        BsonSerializer.SerializerRegistry.GetSerializer<T>(),
        BsonSerializer.SerializerRegistry);
}

internal sealed record MongoObservationDemandGroup(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string? TenantId
);
