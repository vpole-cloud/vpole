using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Users.Migrations;

public sealed class UserIdentityIndexesMigration : IMigration
{
    public ObjectId Id => new("6b100002a213917a38c38002");
    public string Name => "Unique normalized Pole user email";
    public string Description => "Only one verified identity may own an email address.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) => context
        .GetCollection<BsonDocument>("Users")
        .Indexes
        .CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Email"),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken);
}
