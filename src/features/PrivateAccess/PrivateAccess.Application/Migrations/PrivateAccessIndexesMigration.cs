using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.PrivateAccess.Application.Migrations;

public sealed class PrivateAccessIndexesMigration : IMigration
{
    public ObjectId Id => new("6a700047b213917a38c38047");
    public string Name => "Private access grants and sessions";
    public string Description => "Indexes tenant grants and one-use session ticket hashes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        await context.GetCollection<BsonDocument>("PrivateAccessGrants").Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SubjectUserId")
                    .Descending("CreatedAtUtc"),
                new CreateIndexOptions { Name = "IX_PrivateAccessGrants_Tenant_Subject_Created" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId")
                    .Ascending("ResourceDeploymentId"),
                new CreateIndexOptions { Name = "IX_PrivateAccessGrants_Tenant_Deployment" })
        ], cancellationToken);
        await context.GetCollection<BsonDocument>("PrivateAccessSessions").Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Ticket.Hash"),
                new CreateIndexOptions { Unique = true, Name = "UX_PrivateAccessSessions_TicketHash" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Grant.Id"),
                new CreateIndexOptions { Name = "IX_PrivateAccessSessions_Tenant_Grant" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("State.CleanedAtUtc"),
                new CreateIndexOptions
                {
                    Name = "IX_PrivateAccessSessions_Cleaned_TTL",
                    ExpireAfter = TimeSpan.FromDays(30)
                })
        ], cancellationToken);
        await context.GetCollection<BsonDocument>("PrivateAccessAudit").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId")
                    .Ascending("GrantId").Descending("AtUtc"),
                new CreateIndexOptions { Name = "IX_PrivateAccessAudit_Tenant_Grant_At" }),
            cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>("PrivateAccessGateways").Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId")
                    .Ascending("DeploymentTargetId"),
                new CreateIndexOptions { Unique = true, Name = "UX_PrivateAccessGateways_Tenant_Target" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("CertificateSha256Pin"),
                new CreateIndexOptions { Unique = true, Name = "UX_PrivateAccessGateways_Pin" })
        ], cancellationToken);
        await context.GetCollection<BsonDocument>("PrivateAccessBrokerOwners").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId")
                    .Ascending("DeploymentTargetId"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_PrivateAccessBrokerOwners_Tenant_Target"
                }), cancellationToken: cancellationToken);
    }
}
