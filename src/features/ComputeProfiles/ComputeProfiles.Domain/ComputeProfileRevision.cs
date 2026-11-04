using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.ComputeProfiles.Domain;

public sealed record ComputeProfileRevision(
    [property: BsonId] string Id,
    string ComputeProfileId,
    long Revision,
    ComputeProfile Snapshot);

public static class ComputeProfileRevisionFactory
{
    public static ComputeProfileRevision Create(ComputeProfile snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ObjectId.TryParse(snapshot.Id, out _) || snapshot.Revision < 1)
        {
            throw new ArgumentException(
                "A profile revision requires a valid profile identity and a positive revision.",
                nameof(snapshot));
        }

        return new ComputeProfileRevision(
            $"{snapshot.Id}/{snapshot.Revision}",
            snapshot.Id,
            snapshot.Revision,
            snapshot);
    }
}
