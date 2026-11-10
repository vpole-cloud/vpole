using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.StorageProfiles.Domain;

public sealed record StorageProfileRevision(
    [property: BsonId] string Id,
    string StorageProfileId,
    long Revision,
    StorageProfile Snapshot
);

public static class StorageProfileRevisionFactory
{
    public static StorageProfileRevision Create(StorageProfile snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ObjectId.TryParse(snapshot.Id, out _) || snapshot.Revision < 1)
            throw new ArgumentException(
                "A profile revision requires a valid profile identity and a positive revision.",
                nameof(snapshot));

        return new StorageProfileRevision(
            $"{snapshot.Id}/{snapshot.Revision}",
            snapshot.Id,
            snapshot.Revision,
            snapshot);
    }
}
