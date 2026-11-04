using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitOpsBsonSerializationTests
{
    [Fact]
    public void Domain_document_attributes_preserve_current_round_trips_without_feature_registration()
    {
        var now = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        const string url = "https://example.test/source";

        RoundTrip(new ManualSnapshotSource("source", "tenant", "creation", url, now));
        RoundTrip(new ManagedDeclaration(
            "tenant/Widget/demo", "tenant", "Widget", "demo", "source", url,
            "resource", "desired", "applied", null, false, "run", "Prepare", 0));
        RoundTrip(new GitOperationReceipt(
            "receipt", "tenant", "source", "run", 1, "Widget", "demo",
            "Prepare", "resource", "desired", null, false));
        RoundTrip(new GitSourceExecution(
            "execution", "tenant", "source", 1, 0, "run", "commit", "commit", null,
            new ApplyingSnapshot(), [], "[]", url, "actor", "actor@example.test", "pole.io/v1alpha1", [], 0, null));
    }

    public static IEnumerable<object[]> ProgressCases() =>
    [
        [new ApplyingSnapshot(), "applying"],
        [new BlockedSnapshot(), "blocked"],
        [new AppliedSnapshot(), "applied"],
        [new UnsupportedSnapshotSchema(), "unsupported-schema"],
        [new StaleSnapshotExecution(), "stale"]
    ];

    [Theory]
    [MemberData(nameof(ProgressCases))]
    public void Domain_progress_round_trips_through_standard_BSON_discriminators(
        SnapshotProgress progress,
        string expectedDiscriminator)
    {
        var execution = new GitSourceExecution(
            "execution", "tenant", "source", 1, 0, "run", "commit", "commit", null,
            progress, [], "[]", "https://example.test/source", "actor", "actor@example.test",
            "pole.io/v1alpha1", [], 0, null);

        var document = execution.ToBsonDocument();
        Assert.Equal(BsonType.Document, document["Status"].BsonType);
        Assert.Equal(expectedDiscriminator, document["Status"]["_t"].AsBsonArray.Last().AsString);
        Assert.Equal(progress, BsonSerializer.Deserialize<GitSourceExecution>(document).Status);
    }

    private static void RoundTrip<T>(T model) where T : notnull
    {
        var document = model.ToBsonDocument();
        Assert.True(document.Contains("_id"));
        Assert.False(document.Contains("Id"));
        Assert.Equal(BsonType.String, document["_id"].BsonType);

        var restored = BsonSerializer.Deserialize<T>(document);
        Assert.Equal(document, restored.ToBsonDocument());
    }
}
