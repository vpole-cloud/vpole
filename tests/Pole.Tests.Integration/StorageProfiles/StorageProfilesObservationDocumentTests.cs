using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.StorageProfiles.Domain.Observation;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class StorageProfilesObservationDocumentTests
{
    [Theory]
    [InlineData("control")]
    [InlineData("demand")]
    [InlineData("snapshot")]
    public void Current_documents_round_trip_with_explicit_data_and_BSON_identity(string kind)
    {
        var value = Create(kind);
        var type = value.GetType();
        var document = value.ToBsonDocument(type);
        var restored = BsonSerializer.Deserialize(document, type);

        Assert.Equal(document, restored.ToBsonDocument(type));
        Assert.DoesNotContain("_t", document.Names);
        Assert.Equal(kind == "demand" ? BsonType.String : BsonType.ObjectId, document["_id"].BsonType);
        Assert.All(document.Elements.Where(element => element.Name.EndsWith("AtUtc", StringComparison.Ordinal)),
            element => Assert.True(element.Value.IsBsonNull || element.Value.BsonType == BsonType.DateTime));
    }

    internal static object Create(string kind)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var utc = DateTime.UnixEpoch;
        var inventory = new StoredStorageInventory(
            [new("local", "rancher.io/local-path", true, "Delete", "WaitForFirstConsumer", true)],
            [new("volume", "1Gi", StoredVolumeStatus.Bound, "local", "namespace", "claim", "Delete",
                new(null, null, null, null, null))],
            [new("claim", "namespace", "1Gi", "1Gi", StoredClaimStatus.Bound, "local", "volume",
                new(null, null, null, null, null))], true);
        return kind switch
        {
            "control" => new StorageObservationControlDocument(id, 1, 6, 7, 8, 9,
                "replica", "lease-token", utc.AddMinutes(1), true, "source", null, utc),
            "demand" => new StorageObservationDemandDocument($"{id}/interest", id,
                "interest", "connection", "session", utc.AddMinutes(5), utc.AddMinutes(1)),
            "snapshot" => new StorageObservationSnapshotDocument(id, 1, "source", 7, 8, utc, inventory, null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

}
