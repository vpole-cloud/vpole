using Pole.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.WebServices.Migrations;

public sealed class WebServiceReleaseHistoryMigration : IMigration
{
    public ObjectId Id => new("69d00000b213917a38c379ea");
    public string Name => "Web service release history";
    public string Description => "Creates multi-target web service release history indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var releases = context.GetCollection<BsonDocument>("WebServiceReleases");

        await releases.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("WebServiceId")
                    .Ascending("ResourceRevision"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "IX_WebServiceReleases_Tenant_Service_ResourceRevision"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("WebServiceId")
                    .Descending("RequestedAtUtc"),
                new CreateIndexOptions
                {
                    Name = "IX_WebServiceReleases_Tenant_Service_RequestedAt"
                })
        ], cancellationToken);

    }
}
