using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.ResourceDeployments.Application.Persistence.Observation;
using Pole.Features.StorageProfiles.Application.Persistence.Observation;
using Xunit;

namespace Pole.Tests.Integration.Observation;

public sealed class ObservationDocumentCompatibilityTests
{
    // Hand-written historical wire shapes, independent of the current document serializers.
    [Theory]
    [InlineData("runtime-control")]
    [InlineData("runtime-demand")]
    [InlineData("runtime-snapshot")]
    [InlineData("storage-control")]
    [InlineData("storage-demand")]
    [InlineData("storage-snapshot")]
    public void HistoricalDocumentsRoundTripWithoutSchemaChanges(string kind)
    {
        var (type, original) = Legacy(kind);
        var value = BsonSerializer.Deserialize(original, type);
        var written = value.ToBsonDocument(type);
        Assert.Equal(original, written);
        Assert.DoesNotContain("_t", written.Names);
        Assert.All(written.Elements.Where(item => item.Name.EndsWith("AtUtc", StringComparison.Ordinal)),
            item => Assert.True(item.Value.IsBsonNull || item.Value.BsonType == BsonType.DateTime));
        if (kind.EndsWith("demand", StringComparison.Ordinal))
        {
            Assert.Equal(BsonType.String, written["_id"].BsonType);
            Assert.Equal(BsonType.ObjectId, written[kind.StartsWith("runtime", StringComparison.Ordinal)
                ? "DeploymentId" : "DeploymentTargetId"].BsonType);
        }
        else
        {
            Assert.Equal(BsonType.ObjectId, written["_id"].BsonType);
        }
        if (kind.StartsWith("runtime", StringComparison.Ordinal))
            Assert.Equal(BsonType.ObjectId, written["TenantId"].BsonType);
        else
            Assert.DoesNotContain("TenantId", written.Names);
    }

    internal static (Type Type, BsonDocument Document) Legacy(string kind)
    {
        var runtime = kind.StartsWith("runtime", StringComparison.Ordinal);
        var id = ObjectId.Parse("000000000000000000000001");
        var tenant = ObjectId.Parse("000000000000000000000002");
        var utc = new DateTime(2026, 10, 17, 10, 30, 0, DateTimeKind.Utc);
        var document = new BsonDocument("_id", id);
        if (runtime)
            document.Add("TenantId", tenant);
        if (kind.EndsWith("demand", StringComparison.Ordinal))
        {
            document["_id"] = runtime ? $"{tenant}/{id}/interest" : $"{id}/interest";
            document.Add(runtime ? "DeploymentId" : "DeploymentTargetId", id);
            document.Add("SubscriptionId", "interest");
            document.Add("ConnectionId", "connection");
            document.Add("SessionId", "session");
            document.Add("OriginalExpiresAtUtc", utc.AddMinutes(5));
            document.Add("ExpiresAtUtc", utc.AddMinutes(1));
            return (runtime ? typeof(ObservationDemandDocument) : typeof(StorageObservationDemandDocument), document);
        }

        if (runtime)
            document.Add("Pin", new BsonDocument
            {
                { "ResourceType", "pole/web-service" }, { "ResourceId", id.ToString() },
                { "ResourceGroupId", id.ToString() }, { "IsolationScopeId", id.ToString() },
                { "RuntimeCellId", id.ToString() }, { "ResourceRevision", 1L },
                { "DeploymentRevision", 2L }, { "DesiredGeneration", 3L },
                { "DeploymentTargetId", id.ToString() }, { "TargetConnectionVersion", 4L },
                { "TargetWorkloadConfigurationRevision", 5L }, { "KubernetesNamespace", "owned-namespace" }
            });
        else
            document.Add("ConnectionVersion", 4L);

        if (kind.EndsWith("control", StringComparison.Ordinal))
        {
            document.Add("Epoch", 6L);
            document.Add("SourceControlVersion", 7L);
            document.Add("ObservationVersion", 8L);
            document.Add("DemandFence", 9L);
            document.Add("InstanceId", "replica");
            document.Add("Token", "lease-token");
            document.Add("LeaseExpiresAtUtc", utc.AddSeconds(30));
            document.Add("SourceHealthy", true);
            document.Add(runtime ? "BindingId" : "SourceId", "source");
            document.Add("IdleSinceUtc", BsonNull.Value);
            document.Add("UpdatedAtUtc", utc);
            return (runtime ? typeof(ObservationControlDocument) : typeof(StorageObservationControlDocument), document);
        }

        document.Add(runtime ? "BindingId" : "SourceId", "source");
        document.Add("SourceControlVersion", 7L);
        document.Add("ObservationVersion", 8L);
        document.Add("CheckedAtUtc", utc);
        if (runtime)
        {
            document.Add("Observation", new BsonDocument
            {
                { "Kind", "Unavailable" }, { "Reason", "Forbidden" }, { "Observed", BsonNull.Value }
            });
            document.Add("Diagnostics", BsonNull.Value);
            document.Add("Fingerprint", "historical-fingerprint");
        }
        else
        {
            document.Add("Inventory", new BsonDocument
            {
                { "StorageClasses", new BsonArray() }, { "PersistentVolumes", new BsonArray() },
                { "PersistentVolumeClaims", new BsonArray() }, { "Complete", true }
            });
        }
        document.Add("ExpiresAtUtc", BsonNull.Value);
        return (runtime ? typeof(ObservationSnapshotDocument) : typeof(StorageObservationSnapshotDocument), document);
    }
}
