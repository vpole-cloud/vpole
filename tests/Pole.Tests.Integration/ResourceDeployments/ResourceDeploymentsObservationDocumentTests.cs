// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.ResourceDeployments.Domain.Observation;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentsObservationDocumentTests
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
        Assert.All(
            document.Elements.Where(element => element.Name.EndsWith("AtUtc", StringComparison.Ordinal)),
            element => Assert.True(element.Value.IsBsonNull || element.Value.BsonType == BsonType.DateTime));
    }

    internal static object Create(string kind)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var utc = DateTime.UnixEpoch;
        var tenant = ObjectId.GenerateNewId().ToString();
        var pin = new ObservationContextPin("pole/web-service", id, id, id, id, 1, 2, 3, id, 4, 5, "namespace");
        return kind switch
        {
            "control" => new ObservationControlDocument(
                id,
                tenant,
                pin,
                6,
                7,
                8,
                9,
                "replica",
                "lease-token",
                utc.AddMinutes(1),
                true,
                "binding",
                null,
                utc),
            "demand" => new ObservationDemandDocument(
                $"{tenant}/{id}/interest",
                tenant,
                id,
                "interest",
                "connection",
                "session",
                utc.AddMinutes(5),
                utc.AddMinutes(1)),
            "snapshot" => new ObservationSnapshotDocument(
                id,
                tenant,
                pin,
                "binding",
                7,
                8,
                utc,
                new StoredObservation("Unavailable", "Forbidden", null),
                null,
                "fingerprint",
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
