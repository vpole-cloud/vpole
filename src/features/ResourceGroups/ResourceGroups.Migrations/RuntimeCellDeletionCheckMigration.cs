using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.ResourceGroups.Migrations;

public sealed class RuntimeCellDeletionCheckMigration : IMigration
{
    public ObjectId Id => new("69d70002b213917a38c379f6");
    public string Name => "Runtime cell deletion progress index";
    public string Description => "Indexes the persisted due time of deleting runtime cells for recovery.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) =>
        context.GetCollection<BsonDocument>("RuntimeCells").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("State._t").Ascending("State.NextCheckAtUtc"),
                new CreateIndexOptions { Name = "IX_RuntimeCells_State_DeletionCheck" }),
            cancellationToken: cancellationToken);
}
