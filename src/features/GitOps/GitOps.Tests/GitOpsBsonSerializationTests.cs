using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitOps.Domain;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitOpsBsonSerializationTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Current_persistence_attributes_round_trip_without_feature_registration()
    {
        RoundTrip(new ManagedDeclaration("tenant/Widget/demo",
            "tenant",
            "Widget",
            "demo",
            "source",
            "https://example.test/source",
            "resource",
            "desired",
            "applied",
            null,
            false,
            "run",
            "Prepare",
            0));
        RoundTrip(new GitOperationReceipt("receipt",
            "tenant",
            "source",
            "run",
            1,
            "Widget",
            "demo",
            "Prepare",
            "resource",
            "desired",
            null,
            false));
        RoundTrip(new GitAcceptedReference("reference",
            new("tenant", "source"),
            "consumer",
            new("Widget", "demo", "resource")));
        RoundTrip(new GitSourceRequestReceipt("request",
            "tenant",
            "source",
            "key",
            "hash",
            1,
            new(GitRunTrigger.Manual, "actor"),
            Now));
    }

    public static IEnumerable<object[]> StateCases() =>
    [
        [new GitRunWaiting(0)],
        [new GitRunClaimed(new("worker", 1, Now.AddMinutes(2)))],
        [new GitRunDelayed(2, Now.AddMinutes(1))],
        [new GitRunFinished(GitRunCompletion.Failed, Now)],
        [new GitRunFinished(GitRunCompletion.Blocked, Now)],
        [new GitRunFinished(GitRunCompletion.ConfigurationApplied, Now)],
        [new GitRunFinished(GitRunCompletion.AlreadyCurrent, Now)],
        [new GitRunFinished(GitRunCompletion.Superseded, Now)]
    ];

    [Theory]
    [MemberData(nameof(StateCases))]
    public void Every_current_run_state_uses_standard_BSON_discriminators(GitRunState state)
    {
        var run = GitReconciliationRunFactory.Create(new(new("tenant", "source"), "run", 1, 1, 1),
                new("Example", "connection", "repository", "main", ""),
                new(GitRunTrigger.Poll, null),
                Now,
                new GitInputUnresolved(),
                null) with
        {
            State = state
        };
        var document = run.ToBsonDocument();
        Assert.Equal(BsonType.Document, document["State"].BsonType);
        Assert.Equal(state.GetType().Name, document["State"]["_t"].AsBsonArray.Last().AsString);
        Assert.Equal(state, BsonSerializer.Deserialize<GitReconciliationRun>(document).State);
        RoundTrip(run);
    }

    private static void RoundTrip<T>(T model) where T : notnull
    {
        var document = model.ToBsonDocument();
        Assert.True(document.Contains("_id"));
        Assert.False(document.Contains("Id"));
        Assert.Equal(BsonType.String, document["_id"].BsonType);
        Assert.Equal(document, BsonSerializer.Deserialize<T>(document).ToBsonDocument());
    }
}
