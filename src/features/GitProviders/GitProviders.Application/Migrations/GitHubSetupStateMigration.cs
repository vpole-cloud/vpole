using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.GitProviders.Application.Migrations;

public sealed class GitHubSetupStateMigration : IMigration
{
    public ObjectId Id => new("69d10001b213917a38c379ec");
    public string Name => "GitHub setup polymorphic state";
    public string Description => "Replaces setup-state indexes for the polymorphic state document.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var setupAttempts = context.GetCollection<BsonDocument>("GitHubSetupAttempts");

        // Setup attempts expire after ten minutes and cannot continue across the state-model change.
        await setupAttempts.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken);
        await setupAttempts.Indexes.DropOneAsync("UX_GitHubSetupAttempts_InstallState", cancellationToken);
        await setupAttempts.Indexes.DropOneAsync("UX_GitHubSetupAttempts_OAuthState", cancellationToken);

        await setupAttempts.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("State.InstallStateHash"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Sparse = true,
                    Name = "UX_GitHubSetupAttempts_InstallState"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("State.OAuthStateHash"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Sparse = true,
                    Name = "UX_GitHubSetupAttempts_OAuthState"
                })
        ], cancellationToken);
    }
}
