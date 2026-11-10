using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.ContainerRegistries.Migrations;

public sealed class ContainerRegistryMigration : IMigration
{
    public ObjectId Id => new("69d20000b213917a38c379ed");
    public string Name => "Container registry projects and credentials";
    public string Description => "Creates tenant registry project and protected credential indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var projects = context.GetCollection<BsonDocument>("RegistryProjects");
        await projects.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Provider"),
                    new CreateIndexOptions { Unique = true, Name = "UX_RegistryProjects_Tenant_Provider" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("Provider").Ascending("Name"),
                    new CreateIndexOptions { Unique = true, Name = "UX_RegistryProjects_Provider_Name" }),
            ],
            cancellationToken);

        var credentials = context.GetCollection<BsonDocument>("RegistryCredentials");
        await credentials.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("RegistryProjectId")
                        .Ascending("Purpose"),
                    new CreateIndexOptions { Unique = true, Name = "UX_RegistryCredentials_Project_Purpose" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                    new CreateIndexOptions { Name = "IX_RegistryCredentials_ExpiresAtUtc", Sparse = true }),
            ],
            cancellationToken);
    }
}
