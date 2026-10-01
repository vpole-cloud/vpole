using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.DeploymentTargets.Application.Migrations;

public sealed class DeploymentTargetConnectorMigration : IMigration
{
    public ObjectId Id => new("6a700048b213917a38c38048");
    public string Name => "Deployment target connector identities";
    public string Description => "Indexes target-bound enrollment tokens and exact connector certificate pins.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var connectors = context.GetCollection<BsonDocument>("DeploymentTargetConnectors");
        await connectors.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("CertificateFingerprintSha256"),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    Name = "UX_DeploymentTargetConnectors_CertificateFingerprintSha256"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TargetId")
                    .Ascending("State._t"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "IX_DeploymentTargetConnectors_Target_State"
                })
        ], cancellationToken);

        var enrollments = context.GetCollection<BsonDocument>("DeploymentTargetConnectorEnrollments");
        await enrollments.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TokenDigestSha256"),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    Name = "UX_DeploymentTargetConnectorEnrollments_TokenDigestSha256"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TargetId")
                    .Ascending("ConnectorId"),
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "IX_DeploymentTargetConnectorEnrollments_Target_Connector"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                new CreateIndexOptions<BsonDocument>
                {
                    ExpireAfter = TimeSpan.FromDays(1),
                    Name = "IX_DeploymentTargetConnectorEnrollments_Expiry"
                })
        ], cancellationToken);
    }
}
