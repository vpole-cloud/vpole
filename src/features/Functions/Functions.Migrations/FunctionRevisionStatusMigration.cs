// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Functions.Migrations;

public sealed class FunctionRevisionStatusMigration : IMigration
{
    private const string ActiveRevisionIndex = "UX_FunctionRevisions_Tenant_App_Active";
    private const string LegacyLifecycleIndex = "IX_FunctionRevisions_State_Tenant_App";
    private const string LifecycleIndex = "IX_FunctionRevisions_Status_Tenant_App";

    public ObjectId Id => new("69d50000b213917a38c379f3");
    public string Name => "Function revision status hierarchy";

    public string Description =>
        "Migrates revision enum states to polymorphic statuses and rebuilds lifecycle indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var revisions = context.GetCollection<BsonDocument>("FunctionRevisions");
        var legacyRevisions = await revisions.Find(Builders<BsonDocument>.Filter.Exists("State"))
            .ToListAsync(cancellationToken);

        foreach (var revision in legacyRevisions)
        {
            var status = ToStatus(revision);
            var result = await revisions.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("_id", revision["_id"]),
                    Builders<BsonDocument>.Filter.Eq("State", revision["State"])),
                Builders<BsonDocument>.Update.Set("Status", status).Unset("State").Unset("ReadyAtUtc").Unset("Failure"),
                cancellationToken: cancellationToken);
            if (result.MatchedCount != 1)
                throw new InvalidOperationException(
                    "A Function revision changed while migrating its lifecycle status.");
        }

        var indexDocuments = await (await revisions.Indexes.ListAsync(cancellationToken))
            .ToListAsync(cancellationToken);
        var indexNames = indexDocuments.Select(index => index["name"].AsString).ToHashSet(StringComparer.Ordinal);
        foreach (var indexName in new[] { ActiveRevisionIndex, LegacyLifecycleIndex, LifecycleIndex })
            if (indexNames.Contains(indexName))
                await revisions.Indexes.DropOneAsync(indexName, cancellationToken);

        await revisions.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("FunctionAppId"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Name = ActiveRevisionIndex,
                        Unique = true,
                        PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("Status._t", "active"),
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("Status._t")
                        .Ascending("TenantId")
                        .Ascending("FunctionAppId"),
                    new CreateIndexOptions { Name = LifecycleIndex }),
            ],
            cancellationToken);
    }

    private static BsonDocument ToStatus(BsonDocument revision)
    {
        var discriminator = StateName(revision["State"]);
        var status = new BsonDocument
        {
            ["_t"] = new BsonArray { "FunctionRevisionStatus", discriminator },
        };

        if (discriminator is "ready" or "active" or "draining" or "retired")
        {
            if (!revision.TryGetValue("ReadyAtUtc", out var readyAtUtc) || readyAtUtc.IsBsonNull)
                throw new InvalidOperationException("A ready Function revision status requires ReadyAtUtc.");
            status["ReadyAtUtc"] = readyAtUtc.DeepClone();
        }
        else if (discriminator == "failed")
        {
            if (!revision.TryGetValue("Failure", out var failure) || failure.IsBsonNull)
                throw new InvalidOperationException("A failed Function revision status requires Failure.");
            status["Failure"] = failure.DeepClone();
        }

        return status;
    }

    private static string StateName(BsonValue state) => state.BsonType switch
    {
        BsonType.Int32 => state.AsInt32 switch
        {
            0 => "provisioning",
            1 => "ready",
            2 => "active",
            3 => "draining",
            4 => "failed",
            5 => "retired",
            _ => throw UnknownState(state),
        },
        BsonType.String => state.AsString switch
        {
            "Provisioning" => "provisioning",
            "Ready" => "ready",
            "Active" => "active",
            "Draining" => "draining",
            "Failed" => "failed",
            "Retired" => "retired",
            _ => throw UnknownState(state),
        },
        _ => throw UnknownState(state),
    };

    private static InvalidOperationException UnknownState(BsonValue state) =>
        new($"Unknown legacy Function revision state '{state}'.");
}
