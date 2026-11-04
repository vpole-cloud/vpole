using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using Pole.Features.ResourceDeployments.Domain.Observation;

try
{
    var input = new StringBuilder();
    var buffer = new char[4096];
    int read;
    while ((read = await Console.In.ReadAsync(buffer)) > 0)
    {
        if (input.Length + read > 2_000_000)
            return 2;
        input.Append(buffer, 0, read);
    }

    var bytes = Convert.FromBase64String(input.ToString());
    var type = typeof(ObservationSnapshotDocument);
    // No serialization or instance construction of this type precedes the cold read.
    var original = BsonSerializer.Deserialize<BsonDocument>(bytes);
    using var reader = new BsonDocumentReader(original);
    var value = BsonSerializer.Deserialize(reader, type);
    var roundTrip = value.ToBsonDocument(type);
    if (!original.Equals(roundTrip))
        return 3;
    var observationKind = roundTrip["Observation"]["Kind"].AsString;
    var diagnosticsKind = roundTrip["Diagnostics"].IsBsonNull ? "Absent" : roundTrip["Diagnostics"]["Kind"].AsString;
    if (observationKind is not "Observed" and not "Unavailable" ||
        diagnosticsKind is not "Available" and not "Unavailable" and not "Absent")
        return 4;
    var pods = observationKind == "Observed" ? roundTrip["Observation"]["Observed"]["Pods"].AsBsonArray.Count : 0;
    var instances = diagnosticsKind == "Available"
        ? roundTrip["Diagnostics"]["Available"]["Instances"].AsBsonArray.Count
        : 0;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ObservationKind = observationKind,
        DiagnosticsKind = diagnosticsKind,
        Pods = pods,
        Instances = instances,
        Hash = Convert.ToHexString(SHA256.HashData(roundTrip.ToBson()))
    }));
    return 0;
}
catch
{
    Console.Error.WriteLine("Observation serialization probe failed.");
    return 1;
}
