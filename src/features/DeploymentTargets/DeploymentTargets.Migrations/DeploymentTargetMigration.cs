using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.DeploymentTargets.Migrations;

public sealed class DeploymentTargetMigration : IMigration
{
    public ObjectId Id => new("69d60000b213917a38c379f4");
    public string Name => "Deployment targets and platform default";
    public string Description => "Creates unique cluster identity, workload admission, and default-target indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var targets = context.GetCollection<BsonDocument>("DeploymentTargets");
        await targets.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TechnicalName"),
                    new CreateIndexOptions { Unique = true, Name = "UX_DeploymentTargets_TechnicalName" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("ClusterIdentity"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Unique = true,
                        Name = "UX_DeploymentTargets_ClusterIdentity",
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("Enabled")
                        .Ascending("AcceptsWorkloads")
                        .Ascending("DisplayName"),
                    new CreateIndexOptions
                    {
                        Name = "IX_DeploymentTargets_WorkloadAdmission_DisplayName",
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("WorkloadConfiguration.Ingress.PublicBaseDomain"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = new BsonDocument
                        {
                            ["WorkloadConfiguration.Ingress.PublicBaseDomain"] = new BsonDocument("$type", "string"),
                            ["DeletionRequestedAtUtc"] = BsonNull.Value,
                        },
                        Name = "UX_DeploymentTargets_Active_PublicBaseDomain",
                    }),
            ],
            cancellationToken);

        await context.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["collMod"] = "DeploymentTargets",
                ["validator"] = new BsonDocument(
                    "$jsonSchema",
                    new BsonDocument
                    {
                        ["bsonType"] = "object",
                        ["required"] = new BsonArray { "TechnicalName" },
                        ["properties"] = new BsonDocument(
                            "TechnicalName",
                            new BsonDocument
                            {
                                ["bsonType"] = "string",
                                ["minLength"] = 1,
                                ["maxLength"] = 63,
                                ["pattern"] = "^[a-z](?:[a-z0-9-]*[a-z0-9])?$",
                            }),
                    }),
                ["validationLevel"] = "strict",
                ["validationAction"] = "error",
            },
            cancellationToken: cancellationToken);

        var settings = context.GetCollection<BsonDocument>("DeploymentTargetSettings");
        await settings.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("DefaultDeploymentTargetId"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_DeploymentTargetSettings_DefaultDeploymentTargetId",
                }),
            cancellationToken: cancellationToken);
    }
}
