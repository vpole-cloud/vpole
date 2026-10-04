using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application;
using Pole.Features.SourceBuilds.Application.Endpoints.Jobs;
using Pole.Features.SourceBuilds.Application.Persistence.Logs;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildLogApiTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Viewer_reads_stable_incremental_ranges_after_owner_deletion_and_guesses_are_denied()
    {
        var job = CreateJob(TestData.TenantId);
        await Fixture.DbContext.SourceBuildJobs.InsertOneAsync(job);
        var now = DateTime.UtcNow;
        var log = SourceBuildLogFactory.Create(job.Id, job.TenantId, job.WebServiceId, "writer", new(1024 * 1024, 2048, 4096, 7), now);
        for (var sequence = 1; sequence <= 50; sequence++) log = log.Append(sequence, SourceBuildLogStream.StandardError, $"line-{sequence}\n", now);
        log = log.Seal(now);
        await Logs.InsertOneAsync(log);
        var tenant = await Fixture.DbContext.Tenants.Find(x => x.Id == TestData.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(x => x.Id == tenant.Id, tenant with { Members = ImmutableList.Create(TenantFactory.CreateMember(TestData.TenantId, TestData.UserId, TestData.UserEmail, TenantRole.Viewer)) });
        // No configuration or WebService exists: retained job identity is authoritative.
        var first = (await Client.GetFromJsonAsync<SourceBuildLogsDto>(Url(job)))!;
        Assert.Equal("Completed", first.CaptureState);
        Assert.Equal(32, first.Entries.Count);
        Assert.Equal(32, first.NextSequence);
        Assert.True(first.HasMore);
        var second = (await Client.GetFromJsonAsync<SourceBuildLogsDto>(Url(job, 32)))!;
        var repeat = (await Client.GetFromJsonAsync<SourceBuildLogsDto>(Url(job, 32)))!;
        Assert.Equal(Enumerable.Range(33, 18).Select(x => (long)x), second.Entries.Select(x => x.Sequence));
        Assert.Equal(second.Entries.Select(x => x.Text), repeat.Entries.Select(x => x.Text));
        Assert.False(second.HasMore);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync(Url(job, 51))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(Url(job).Replace(job.WebServiceId, ObjectId.GenerateNewId().ToString()))).StatusCode);
        var foreign = CreateJob(ObjectId.GenerateNewId().ToString());
        await Fixture.DbContext.SourceBuildJobs.InsertOneAsync(foreign);
        await Logs.InsertOneAsync(SourceBuildLogFactory.Create(foreign.Id, foreign.TenantId, foreign.WebServiceId, "foreign", log.Limits, now));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(Url(foreign))).StatusCode);
        await Fixture.DbContext.Tenants.ReplaceOneAsync(x => x.Id == tenant.Id, tenant with { Members = [] });
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.GetAsync(Url(job))).StatusCode);
    }

    [Fact]
    public async Task Ongoing_capture_is_read_incrementally_and_reader_cancellation_does_not_fence_the_writer()
    {
        var job = CreateJob(TestData.TenantId);
        await Fixture.DbContext.SourceBuildJobs.InsertOneAsync(job);
        using var services = Services(TimeProvider.System);
        using var scope = services.CreateScope();
        var log = SourceBuildLogFactory.Create(job.Id, job.TenantId, job.WebServiceId, "worker", new(4096, 16, 256, 7), DateTime.UtcNow);
        await scope.ServiceProvider.GetRequiredService<StartSourceBuildLog>()(log);
        var identity = new SourceBuildLogIdentity(job.TenantId, job.WebServiceId, job.Id, log.WriterToken);
        var append = scope.ServiceProvider.GetRequiredService<AppendSourceBuildLog>();
        await append(identity, 1, SourceBuildLogStream.StandardOutput, "live first\n");
        var live = (await Client.GetFromJsonAsync<SourceBuildLogsDto>(Url(job)))!;
        Assert.Equal("Open", live.CaptureState);
        Assert.Equal(1, live.NextSequence);
        using (var canceledRead = new CancellationTokenSource())
        {
            await canceledRead.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client.GetAsync(Url(job, 1), canceledRead.Token));
        }
        var renewedAndAppended = await Task.WhenAll(scope.ServiceProvider.GetRequiredService<RenewSourceBuildLog>()(identity), append(identity, 2, SourceBuildLogStream.StandardOutput, "still running\n"));
        Assert.All(renewedAndAppended, result => Assert.IsType<SourceBuildLogWriteAccepted>(result.Value));
        var resumed = (await Client.GetFromJsonAsync<SourceBuildLogsDto>(Url(job, 1)))!;
        Assert.Equal(2, Assert.Single(resumed.Entries).Sequence);
        Assert.Equal("Open", resumed.CaptureState);
        Assert.Equal(job.UpdatedAtUtc, (await Fixture.DbContext.SourceBuildJobs.Find(x => x.Id == job.Id).SingleAsync()).UpdatedAtUtc, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Mongo_appends_are_atomic_idempotent_across_service_restart_and_writer_identity_fenced()
    {
        var job = CreateJob(TestData.TenantId);
        var now = DateTimeOffset.UtcNow;
        var clock = new MutableClock(now);
        var identity = new SourceBuildLogIdentity(job.TenantId, job.WebServiceId, job.Id, "writer");
        using (var services = Services(clock))
        using (var scope = services.CreateScope())
        {
            var start = scope.ServiceProvider.GetRequiredService<StartSourceBuildLog>();
            Assert.IsType<SourceBuildLogWriteAccepted>((await start(SourceBuildLogFactory.Create(job.Id, job.TenantId, job.WebServiceId, identity.WriterToken, new(4096, 16, 256, 7), now.UtcDateTime))).Value);
            var append = scope.ServiceProvider.GetRequiredService<AppendSourceBuildLog>();
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => append(identity, 1, SourceBuildLogStream.StandardOutput, "first")));
            Assert.All(results, result => Assert.IsType<SourceBuildLogWriteAccepted>(result.Value));
        }
        using var restarted = Services(clock);
        using var restartedScope = restarted.CreateScope();
        var writer = restartedScope.ServiceProvider.GetRequiredService<AppendSourceBuildLog>();
        Assert.IsType<SourceBuildLogWriteAccepted>((await writer(identity, 1, SourceBuildLogStream.StandardOutput, "first")).Value);
        Assert.IsType<SourceBuildLogWriteFenced>((await writer(identity, 1, SourceBuildLogStream.StandardOutput, "different")).Value);
        Assert.IsType<SourceBuildLogWriteFenced>((await writer(identity with { TenantId = ObjectId.GenerateNewId().ToString() }, 2, SourceBuildLogStream.StandardOutput, "foreign")).Value);
        Assert.IsType<SourceBuildLogWriteFenced>((await writer(identity with { WriterToken = "stale" }, 2, SourceBuildLogStream.StandardOutput, "foreign")).Value);
        for (var sequence = 2; sequence <= 16; sequence++) await writer(identity, sequence, SourceBuildLogStream.StandardOutput, new string('x', 256));
        await writer(identity, 17, SourceBuildLogStream.StandardOutput, "discarded");
        var stored = await Logs.Find(x => x.Id == job.Id).SingleAsync();
        Assert.True(stored.Truncated);
        Assert.Equal(16, stored.Entries.Count);
        Assert.Equal(16, stored.LastSequence);
        Assert.True(stored.CapturedBytes <= 4096);
        clock.Now = now.AddMinutes(3);
        Assert.IsType<SourceBuildLogWriteFenced>((await writer(identity, 17, SourceBuildLogStream.StandardOutput, "late")).Value);
        await restartedScope.ServiceProvider.GetRequiredService<RecoverSourceBuildLogs>()();
        Assert.IsType<InterruptedSourceBuildLog>((await Logs.Find(x => x.Id == job.Id).SingleAsync()).State);
        clock.Now = now.AddDays(8);
        await restartedScope.ServiceProvider.GetRequiredService<RecoverSourceBuildLogs>()();
        var expired = await Logs.Find(x => x.Id == job.Id).SingleAsync();
        Assert.IsType<ExpiredSourceBuildLog>(expired.State);
        Assert.Empty(expired.Entries);
        Assert.Equal(16, expired.LastSequence);
    }

    [Fact]
    public async Task Read_enforces_expiry_and_reports_legacy_unavailable_without_resetting_ahead_cursor()
    {
        var job = CreateJob(TestData.TenantId);
        await Fixture.DbContext.SourceBuildJobs.InsertOneAsync(job);
        Assert.Equal("Unavailable", (await Client.GetFromJsonAsync<SourceBuildLogsDto>(Url(job)))!.CaptureState);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync(Url(job, 1))).StatusCode);
        var before = DateTime.UtcNow.AddDays(-8);
        await Logs.InsertOneAsync(SourceBuildLogFactory.Create(job.Id, job.TenantId, job.WebServiceId, "writer", new(4096, 16, 256, 7), before).Append(1, SourceBuildLogStream.StandardOutput, "expired-canary", before).Seal(before));
        var expired = (await Client.GetFromJsonAsync<SourceBuildLogsDto>(Url(job)))!;
        Assert.Equal("Expired", expired.CaptureState);
        Assert.Empty(expired.Entries);
        Assert.Equal(1, expired.LastSequence);
        Assert.False(expired.HasMore);
        Assert.DoesNotContain("expired-canary", await Client.GetStringAsync(Url(job)));
    }

    private IMongoCollection<SourceBuildLog> Logs => Fixture.Database.GetCollection<SourceBuildLog>("SourceBuildLogs");
    private ServiceProvider Services(TimeProvider clock) => new ServiceCollection().AddLogging().AddSingleton(Fixture.Database).AddSingleton(clock)
        .AddSourceBuildsFeature(new ConfigurationBuilder().Build()).BuildServiceProvider(validateScopes: true);
    private static string Url(SourceBuildJob job, long after = 0) => $"/api/v1/source-build-jobs/{job.Id}/logs?webServiceId={job.WebServiceId}&afterSequence={after}";
    private static SourceBuildJob CreateJob(string tenantId) => SourceBuildJobFactory.CreateManual(tenantId, ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), "main", "abc123",
        new("candidate", SourceBuildRecipeKind.Dockerfile, ".", "Dockerfile", [], null, "FROM scratch", false), TestData.UserId, TestData.UserEmail, new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
