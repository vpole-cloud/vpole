using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Execution.Queueing;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.SourceBuilds.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

[Collection(IntegrationTestCollection.Global2)]
public sealed class SourceBuildQueueRecoveryTests(
    MongoReplicaSetFixture fixture
) : IsolatedApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Recovery_EnqueuesOnlyStaleQueuedJobs()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var stale = CreateJob(now.UtcDateTime.AddMinutes(-2));
        var recent = CreateJob(now.UtcDateTime.AddSeconds(-10));
        var terminal = CreateJob(now.UtcDateTime.AddMinutes(-2))
            .Fail(SourceBuildPhase.Queued, "ExpectedFailure", "Already terminal.", now.UtcDateTime.AddMinutes(-1));
        await Fixture.DbContext.SourceBuildJobs.InsertManyAsync([stale, recent, terminal]);
        var dispatcher = new RecordingDispatcher();
        LoadStaleQueuedSourceBuildJobs loadStale = async (
            cutoff, limit,
            cancellationToken) => await Fixture.DbContext
            .SourceBuildJobs
            .Find(
                Builders<SourceBuildJob>.Filter.And(
                    Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, QueuedSourceBuild>(item => item.State),
                    Builders<SourceBuildJob>.Filter.Lte(item => item.UpdatedAtUtc, cutoff)))
            .SortBy(item => item.UpdatedAtUtc)
            .Limit(limit)
            .Project(item => item.Id)
            .ToListAsync(cancellationToken);
        var recovery = new SourceBuildQueueRecoveryJob(loadStale, dispatcher.Invoke, new FixedTimeProvider(now));

        await recovery.RecoverAsync();

        Assert.Equal(stale.Id, Assert.Single(dispatcher.BuildJobIds));
    }

    private SourceBuildJob CreateJob(DateTime createdAtUtc) => SourceBuildJobFactory.CreateManual(
        Data.TenantId,
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        "main",
        "abc123",
        new SourceBuildInputVersion(1, 1, 1),
        new SourceBuildRecipe("FROM scratch"),
        Data.UserId,
        Data.UserEmail,
        new SourceBuildJobFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), createdAtUtc));

    private sealed class RecordingDispatcher
    {
        public List<string> BuildJobIds { get; } = [];

        public string Invoke(string buildJobId)
        {
            BuildJobIds.Add(buildJobId);
            return $"recorded-{buildJobId}";
        }
    }

    private sealed class FixedTimeProvider(
        DateTimeOffset now
    ) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
