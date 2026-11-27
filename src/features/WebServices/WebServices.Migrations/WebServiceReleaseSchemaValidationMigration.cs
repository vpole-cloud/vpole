// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.WebServices.Migrations;

/// <summary>Historical strict BSON schema for aggregate multi-target release snapshots.</summary>
public sealed class WebServiceReleaseSchemaValidationMigration : IMigration
{
    private const string CollectionName = "WebServiceReleases";
    public ObjectId Id => new("69d90003b213917a38c379f9");
    public string Name => "Web service multi-target release schema validation";
    public string Description => "Applies strict validation to release deployment snapshots and aggregate outcomes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var validator = new BsonDocument(
            "$jsonSchema",
            new BsonDocument
            {
                ["bsonType"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new BsonArray
                {
                    "_id", "TenantId", "WebServiceId", "ResourceRevision", "Action", "Outcome", "ActorUserId",
                    "ActorEmail", "RequestedAtUtc", "CompletedAtUtc", "FailureMessage", "TraceId", "CorrelationId",
                    "Provenance", "Deployments", "Spec", "Exposure",
                },
                ["properties"] = new BsonDocument
                {
                    ["_id"] = new BsonDocument("bsonType", "objectId"),
                    ["TenantId"] = new BsonDocument("bsonType", "string"),
                    ["WebServiceId"] = new BsonDocument("bsonType", "string"),
                    ["ResourceRevision"] = new BsonDocument { ["bsonType"] = "long", ["minimum"] = 1 },
                    ["Action"] = new BsonDocument(
                        "enum",
                        new BsonArray
                        {
                            "Create", "Update", "Redeploy", "Rollback", "ImageTagUpdate", "SourceBuild",
                            "AddDeployment", "UpdateDeployment", "RemoveDeployment", "RetryDeployment",
                            "HostnameChanged",
                        }),
                    ["Outcome"] = new BsonDocument(
                        "enum",
                        new BsonArray { "Pending", "Succeeded", "PartiallyFailed", "Failed", "Superseded" }),
                    ["ActorUserId"] = NullableString(),
                    ["ActorEmail"] = NullableString(),
                    ["RequestedAtUtc"] = new BsonDocument("bsonType", "date"),
                    ["CompletedAtUtc"] = Nullable("date"),
                    ["FailureMessage"] = NullableString(),
                    ["TraceId"] = NullableString(),
                    ["CorrelationId"] = NullableString(),
                    ["Provenance"] = Nullable("object"),
                    ["Deployments"] = new BsonDocument
                        { ["bsonType"] = "array", ["minItems"] = 1, ["items"] = DeploymentSchema() },
                    ["Spec"] = new BsonDocument("bsonType", "object"),
                    ["Exposure"] = new BsonDocument
                    {
                        ["bsonType"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new BsonArray { "Mode", "GeneratedHostnameLabelPrefix" },
                        ["properties"] = new BsonDocument
                        {
                            ["Mode"] = new BsonDocument("enum", new BsonArray { "Private", "Public" }),
                            ["GeneratedHostnameLabelPrefix"] = new BsonDocument("bsonType", "string"),
                        },
                    },
                },
            });
        using var names = await context.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = new BsonDocument("name", CollectionName) },
            cancellationToken);
        if (!await names.AnyAsync(cancellationToken))
        {
            await context.CreateCollectionAsync(
                CollectionName,
                new CreateCollectionOptions<BsonDocument>
                {
                    Validator = new BsonDocumentFilterDefinition<BsonDocument>(validator),
                    ValidationLevel = DocumentValidationLevel.Strict,
                    ValidationAction = DocumentValidationAction.Error,
                },
                cancellationToken);
            return;
        }

        await context.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["collMod"] = CollectionName, ["validator"] = validator, ["validationLevel"] = "strict",
                ["validationAction"] = "error",
            },
            cancellationToken: cancellationToken);
    }

    private static BsonDocument DeploymentSchema() => new()
    {
        ["bsonType"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new BsonArray
        {
            "ResourceDeploymentId", "DeploymentTargetId", "ResourceRevision", "DeploymentGeneration", "Operation",
            "Outcome", "CompletedAtUtc", "FailureCode", "FailureMessage",
        },
        ["properties"] = new BsonDocument
        {
            ["ResourceDeploymentId"] = new BsonDocument("bsonType", "string"),
            ["DeploymentTargetId"] = new BsonDocument("bsonType", "string"),
            ["ResourceRevision"] = new BsonDocument { ["bsonType"] = "long", ["minimum"] = 1 },
            ["DeploymentGeneration"] = new BsonDocument { ["bsonType"] = "long", ["minimum"] = 1 },
            ["Operation"] = new BsonDocument("enum", new BsonArray { "Apply", "Delete" }),
            ["Outcome"] = new BsonDocument("enum", new BsonArray { "Pending", "Succeeded", "Failed", "Removed" }),
            ["CompletedAtUtc"] = Nullable("date"),
            ["FailureCode"] = NullableString(),
            ["FailureMessage"] = NullableString(),
        },
    };

    private static BsonDocument NullableString() => Nullable("string");
    private static BsonDocument Nullable(string type) => new("bsonType", new BsonArray { "null", type });
}
