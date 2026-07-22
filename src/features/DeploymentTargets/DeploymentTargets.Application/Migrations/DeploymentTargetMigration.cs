using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.DeploymentTargets.Application.Migrations;

public sealed class DeploymentTargetMigration : IMigration
{
    public ObjectId Id => new("69d60000b213917a38c379f4");
    public string Name => "Deployment targets and platform default";
    public string Description =>
        "Creates unique cluster identity, workload admission, and default-target indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var targets = context.GetCollection<BsonDocument>("DeploymentTargets");
        await targets.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("ClusterIdentity"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_DeploymentTargets_ClusterIdentity"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("Enabled")
                    .Ascending("AcceptsWorkloads")
                    .Ascending("DisplayName"),
                new CreateIndexOptions
                {
                    Name = "IX_DeploymentTargets_WorkloadAdmission_DisplayName"
                })
        ], cancellationToken);

        var settings = context.GetCollection<BsonDocument>("DeploymentTargetSettings");
        await settings.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("DefaultDeploymentTargetId"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_DeploymentTargetSettings_DefaultDeploymentTargetId"
                }),
            cancellationToken: cancellationToken);
    }
}
