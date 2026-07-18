using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Migrations;

public sealed class FunctionScalingIndexesMigration : IMigration
{
    public ObjectId Id => new("69d50000b213917a38c379f1");
    public string Name => "Function scaling indexes";
    public string Description => "Creates the revision lifecycle index used by the Function scaler.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) =>
        context.GetCollection<FunctionRevision>("FunctionRevisions").Indexes.CreateOneAsync(
            new CreateIndexModel<FunctionRevision>(
                Builders<FunctionRevision>.IndexKeys
                    .Ascending("Status._t")
                    .Ascending(revision => revision.TenantId)
                    .Ascending(revision => revision.FunctionAppId),
                new CreateIndexOptions { Name = "IX_FunctionRevisions_Status_Tenant_App" }),
            cancellationToken: cancellationToken);
}
