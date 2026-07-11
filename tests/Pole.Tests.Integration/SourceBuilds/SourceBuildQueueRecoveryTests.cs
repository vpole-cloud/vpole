using MongoDB.Bson;
using Pole.Features.SourceBuilds.Application.Jobs;
using Pole.Features.SourceBuilds.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildQueueRecoveryTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Recovery_EnqueuesOnlyStaleQueuedJobs()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var stale = CreateJob(now.UtcDateTime.AddMinutes(-2));
        var recent = CreateJob(now.UtcDateTime.AddSeconds(-10));
        var terminal = CreateJob(now.UtcDateTime.AddMinutes(-2)).Fail(
            SourceBuildPhase.Queued,
            "ExpectedFailure",
            "Already terminal.",
            now.UtcDateTime.AddMinutes(-1));
        await Fixture.DbContext.SourceBuildJobs.InsertManyAsync([stale, recent, terminal]);
        var dispatcher = new RecordingDispatcher();
        var recovery = new SourceBuildQueueRecoveryJob(
            Fixture.DbContext,
            dispatcher,
            new FixedTimeProvider(now));

        await recovery.RecoverAsync();

        Assert.Equal(stale.Id, Assert.Single(dispatcher.BuildJobIds));
    }

    private static SourceBuildJob CreateJob(DateTime createdAtUtc) =>
        SourceBuildJobFactory.CreateManual(
            TestData.TenantId,
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "main",
            "abc123",
            new SourceBuildRecipe(
                "candidate",
                SourceBuildRecipeKind.DotNet,
                ".",
                "App.csproj",
                System.Collections.Immutable.ImmutableList.Create("dotnet publish"),
                "dotnet App.dll",
                "FROM scratch",
                false),
            TestData.UserId,
            TestData.UserEmail,
            new SourceBuildJobFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                createdAtUtc));

    private sealed class RecordingDispatcher : ISourceBuildJobDispatcher
    {
        public List<string> BuildJobIds { get; } = [];

        public string Enqueue(string buildJobId)
        {
            BuildJobIds.Add(buildJobId);
            return $"recorded-{buildJobId}";
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
