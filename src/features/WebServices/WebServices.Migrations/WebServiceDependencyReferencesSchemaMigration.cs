// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.WebServices.Migrations;

/// <summary>Additive logical reference fields; legacy documents retain their existing schema and defaults.</summary>
public sealed class WebServiceDependencyReferencesSchemaMigration : IMigration
{
    public ObjectId Id => new("6aef0001b213917a38c38001");
    public string Name => "Web service logical dependency references";

    public string Description =>
        "Allows bounded provider-owned logical connection and Secret key references in workload configuration.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var validator = WebServiceSchemaValidationMigration.CreateValidator();
        var config = validator["$jsonSchema"]["properties"]["Spec"]["properties"]["Config"]["properties"]
            .AsBsonDocument;
        config["ConnectionEnvironmentReferences"] = Map(
            Reference(
                ["ConnectionId", "ValueKind", "ConfigurationKey"],
                ("ConnectionId", ObjectIdString()),
                ("ValueKind", new BsonDocument("enum", new BsonArray { "Host", "Port", "Configuration" })),
                ("ConfigurationKey",
                    new BsonDocument { ["bsonType"] = new BsonArray { "string", "null" }, ["maxLength"] = 128 })));
        config["SecretEnvironmentReferences"] = Map(
            Reference(
                ["SecretId", "Key"],
                ("SecretId", ObjectIdString()),
                ("Key", new BsonDocument
                {
                    ["bsonType"] = "string", ["minLength"] = 1, ["maxLength"] = 253,
                    ["pattern"] = "^[A-Za-z0-9._-]+$",
                })));
        await context.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["collMod"] = "WebServices",
                ["validator"] = validator,
                ["validationLevel"] = "strict",
                ["validationAction"] = "error",
            },
            cancellationToken: cancellationToken);
    }

    private static BsonDocument Map(BsonDocument value) => new()
    {
        ["bsonType"] = "object",
        ["maxProperties"] = 64,
        ["additionalProperties"] = false,
        ["patternProperties"] = new BsonDocument("^[A-Za-z_][A-Za-z0-9_]{0,127}$", value),
    };

    private static BsonDocument Reference(string[] required, params (string Name, BsonDocument Schema)[] fields) =>
        new()
        {
            ["bsonType"] = "object",
            ["required"] = new BsonArray(required),
            ["additionalProperties"] = false,
            ["properties"] = new BsonDocument(fields.Select(field => new BsonElement(field.Name, field.Schema))),
        };

    private static BsonDocument ObjectIdString() => new() { ["bsonType"] = "string", ["pattern"] = "^[0-9a-f]{24}$" };
}
