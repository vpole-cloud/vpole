using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.StorageProfiles.Application.Persistence.Observation;

namespace Pole.Features.StorageProfiles.Application.Migrations;

public sealed class StorageObservationIndexMigration : IMigration
{
    public ObjectId Id => new("6acc0000b213917a38c38108");
    public string Name => "Storage observation interest and snapshot expiry";

    public string Description =>
        "Expires bounded platform discovery interests and inactive snapshots while retaining ownership epochs.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) =>
        StorageObservationIndexes.CreateAsync(context, cancellationToken);
}
