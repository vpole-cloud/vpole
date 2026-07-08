using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.WebServices.Application.Migrations;

public sealed class WebServiceReleaseHistoryMigration : IMigration
{
    public ObjectId Id => new("69d00000b213917a38c379ea");
    public string Name => "Web service release history";
    public string Description => "Backfills web service active revisions and creates release history indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var webServices = context.GetCollection<BsonDocument>("WebServices");
        var releases = context.GetCollection<BsonDocument>("WebServiceReleases");

        await releases.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("WebServiceId")
                    .Ascending("Revision"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "IX_WebServiceReleases_Tenant_Service_Revision"
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

        var existingServices = await webServices
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var service in existingServices)
        {
            if (!service.TryGetValue("_id", out var serviceIdValue) ||
                !service.TryGetValue("TenantId", out var tenantIdValue) ||
                !service.TryGetValue("Spec", out var specValue))
            {
                continue;
            }

            var serviceId = ReadStringId(serviceIdValue);
            var tenantId = tenantIdValue.AsString;

            if (service.GetValue("ActiveRevision", 0).ToInt32() <= 0)
            {
                await webServices.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", serviceIdValue),
                    Builders<BsonDocument>.Update.Set("ActiveRevision", 1),
                    cancellationToken: cancellationToken);
            }

            var releaseFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("TenantId", tenantId),
                Builders<BsonDocument>.Filter.Eq("WebServiceId", serviceId),
                Builders<BsonDocument>.Filter.Eq("Revision", 1));

            var releaseExists = await releases.CountDocumentsAsync(
                releaseFilter,
                cancellationToken: cancellationToken) > 0;

            if (releaseExists)
                continue;

            await releases.InsertOneAsync(
                new BsonDocument
                {
                    ["_id"] = ObjectId.GenerateNewId(),
                    ["TenantId"] = tenantId,
                    ["WebServiceId"] = serviceId,
                    ["Revision"] = 1,
                    ["Action"] = "Imported",
                    ["Outcome"] = "Succeeded",
                    ["ActorUserId"] = BsonNull.Value,
                    ["ActorEmail"] = BsonNull.Value,
                    ["RequestedAtUtc"] = now,
                    ["CompletedAtUtc"] = now,
                    ["FailureMessage"] = BsonNull.Value,
                    ["TraceId"] = BsonNull.Value,
                    ["CorrelationId"] = BsonNull.Value,
                    ["Spec"] = specValue.DeepClone().AsBsonDocument
                },
                cancellationToken: cancellationToken);
        }
    }

    private static string ReadStringId(BsonValue value) =>
        value.BsonType == BsonType.ObjectId
            ? value.AsObjectId.ToString()
            : value.AsString;
}
