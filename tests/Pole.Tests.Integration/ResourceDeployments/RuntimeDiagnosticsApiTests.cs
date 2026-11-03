using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Pole.Tests.Integration.Http;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Diagnostics;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class RuntimeDiagnosticsApiTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    private readonly TestProvider provider = new();
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddSingleton(provider);
        services.AddSingleton(ResourceDeploymentObserverDescriptor.Create<TestProvider>(ResourceTypeId.Create("pole/runtime-test")));
        services.AddSingleton(new RuntimeDiagnosticsOptions { MaximumBytes = 4096, LifetimeSeconds = 10, OwnershipCheckSeconds = 1 });
    }
    private async Task<ResourceDeployment> SeedAsync(string? tenant = null)
    {
        var now = DateTime.UtcNow;
        var deployment = ResourceDeploymentFactory.Create(tenant ?? TestData.TenantId, ResourceTypeId.Create("pole/runtime-test"), ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(),
            ResolvedResourcePlacement.Create(ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), "private-namespace-canary", 1, 1, null, null, ResourceDeploymentTestData.Compute(), now),
            ResourcePlacementCapability.SingleTarget, true, ResourceDeploymentFactory.DefaultOptions with
            {
                Id = ObjectId.GenerateNewId().ToString(),
                CreatedAtUtc = now
            });
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);
        return deployment;
    }
    private static string Url(ResourceDeployment deployment, long? revision = null, string uid = "owned-uid", string container = "app", bool follow = true, int tail = 100) =>
        $"/api/v1/resource-deployments/{deployment.Id}/runtime-logs?instanceUid={uid}&container={container}&expectedRevision={revision ?? deployment.Revision}&follow={follow.ToString().ToLowerInvariant()}&tailLines={tail}";
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    private static async Task<RuntimeLogFrame> FrameAsync(StreamReader reader) => System.Text.Json.JsonSerializer.Deserialize<RuntimeLogFrame>(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new InvalidOperationException("Stream ended without a frame."), Json)!;

    [Fact]
    public async Task Viewer_can_read_owned_recent_output_and_foreign_stale_or_invalid_selection_never_opens()
    {
        var owned = await SeedAsync(); var foreign = await SeedAsync(ObjectId.GenerateNewId().ToString());
        var tenant = await Fixture.DbContext.Tenants.Find(x => x.Id == TestData.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(x => x.Id == tenant.Id, tenant with { Members = ImmutableList.Create(TenantFactory.CreateMember(TestData.TenantId, TestData.UserId, TestData.UserEmail, TenantRole.Viewer)) });
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(Url(foreign))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync(Url(owned, tail: 501))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client.GetAsync(Url(owned, revision: owned.Revision + 1))).StatusCode);
        Assert.Empty(provider.Sessions);
        Assert.Equal("InstanceRemoved", (await (await Client.GetAsync(Url(owned, uid: "foreign-uid"))).Content.ReadFromJsonAsync<RuntimeLogsUnavailable>())!.Reason);
        Assert.Empty(provider.Sessions);
        provider.Block = false;
        using var response = await Client.GetAsync(Url(owned, follow: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var output = await response.Content.ReadAsStringAsync();
        Assert.Contains("controlled-runtime-marker", output); Assert.Contains("EndOfWindow", output);
        Assert.DoesNotContain("private-namespace-canary", output);
        Assert.True(Assert.Single(provider.Sessions).Disposed.Task.IsCompleted);
    }

    [Fact]
    public async Task Closing_response_cancels_upstream_and_releases_all_four_stream_slots()
    {
        var deployment = await SeedAsync();
        for (var i = 0; i < 5; i++)
        {
            using var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(deployment)), HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            Assert.Equal("Opened", (await FrameAsync(reader)).Kind);
            Assert.Contains("controlled-runtime-marker", (await FrameAsync(reader)).Text);
            var session = provider.Sessions.Last();
            // Receiving output does not guarantee the next upstream read has started.
            await session.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            reader.Dispose(); response.Dispose();
            await session.ReadCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(5, provider.Sessions.Count);
    }

    [Fact]
    public async Task Application_shutdown_cancels_and_disposes_the_upstream_without_waiting_for_the_stream_limit()
    {
        var deployment = await SeedAsync();
        using var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(deployment)), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await FrameAsync(reader); await FrameAsync(reader);
        var session = Assert.Single(provider.Sessions);
        await session.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await StopApiAsync(shutdown.Token);
        await session.ReadCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false, "InstanceReplaced")]
    [InlineData(true, "Superseded")]
    public async Task Follow_stops_with_explicit_instance_or_persisted_revision_change(bool revisionChanged, string expected)
    {
        var deployment = await SeedAsync();
        using var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(deployment)), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await FrameAsync(reader); await FrameAsync(reader);
        if (revisionChanged) await Fixture.DbContext.ResourceDeployments.UpdateOneAsync(x => x.Id == deployment.Id, Builders<ResourceDeployment>.Update.Inc(x => x.Revision, 1));
        else provider.Replaced = true;
        var ended = await FrameAsync(reader); Assert.Equal("Ended", ended.Kind); Assert.Equal(expected, ended.Reason);
        await Assert.Single(provider.Sessions).Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Fifth_concurrent_reader_is_rejected_and_a_closed_reader_admits_a_new_read()
    {
        var deployment = await SeedAsync();
        var responses = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(deployment)), HttpCompletionOption.ResponseHeadersRead);
                responses.Add(response);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            using var fifth = await Client.GetAsync(Url(deployment));
            Assert.Equal(HttpStatusCode.Conflict, fifth.StatusCode);
            Assert.Equal("CapacityBusy", (await fifth.Content.ReadFromJsonAsync<RuntimeLogsUnavailable>())!.Reason);
            Assert.Equal(4, provider.Sessions.Count);
            responses[0].Dispose();
            await provider.Sessions.First().Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var next = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(deployment)), HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, next.StatusCode); Assert.Equal(5, provider.Sessions.Count);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Target_transport_failure_during_follow_emits_safe_terminal_reason_and_disposes_upstream()
    {
        var deployment = await SeedAsync();
        using var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(deployment)), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await FrameAsync(reader); await FrameAsync(reader); provider.Outage = true;
        var ended = await FrameAsync(reader);
        Assert.Equal("Ended", ended.Kind); Assert.Equal("TargetUnavailable", ended.Reason);
        await Assert.Single(provider.Sessions).Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false, "InstanceReplaced")]
    [InlineData(true, "TargetUnavailable")]
    public async Task Upstream_eof_rechecks_ownership_and_transport_before_reporting_completion(bool outage, string reason)
    {
        var deployment = await SeedAsync(); provider.Block = false;
        provider.AtEof = () => { if (outage) provider.Outage = true; else provider.Replaced = true; };
        using var response = await Client.GetAsync(Url(deployment));
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => System.Text.Json.JsonSerializer.Deserialize<RuntimeLogFrame>(line, Json)!).ToArray();
        Assert.Equal("Ended", lines[^1].Kind); Assert.Equal(reason, lines[^1].Reason);
        Assert.True(Assert.Single(provider.Sessions).Disposed.Task.IsCompleted);
    }

    [Fact]
    public async Task A_stalled_periodic_native_check_reports_timeout_and_cancels_output_before_the_lifetime_limit()
    {
        var deployment = await SeedAsync();
        using var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(deployment)), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await FrameAsync(reader); await FrameAsync(reader);
        var session = Assert.Single(provider.Sessions);
        await session.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.StalledVerification = true;
        var ended = await FrameAsync(reader);
        Assert.Equal("Ended", ended.Kind); Assert.Equal("Timeout", ended.Reason);
        await session.ReadCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Output_is_unicode_safe_bounded_and_lifetime_is_finite_even_without_more_output()
    {
        var deployment = await SeedAsync(); provider.Block = false; provider.Output = string.Concat(Enumerable.Repeat("🙂", 2000));
        using (var response = await Client.GetAsync(Url(deployment)))
        {
            var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => System.Text.Json.JsonSerializer.Deserialize<RuntimeLogFrame>(line, Json)!).ToArray();
            Assert.Equal("OutputLimit", lines[^1].Reason); Assert.Equal(4096, lines[^1].OutputBytes);
            Assert.DoesNotContain('\uFFFD', string.Concat(lines.Where(frame => frame.Kind == "Output").Select(frame => frame.Text)));
        }
        provider.Block = true; provider.Output = "controlled-runtime-marker\n";
        using var finite = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(deployment)), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await finite.Content.ReadAsStreamAsync());
        await FrameAsync(reader); await FrameAsync(reader);
        Assert.Equal("LifetimeLimit", (await FrameAsync(reader)).Reason);
        await provider.Sessions.Last().Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Target_outage_is_safe_and_reconnect_opens_a_new_window()
    {
        var deployment = await SeedAsync(); provider.Outage = true;
        using var unavailable = await Client.GetAsync(Url(deployment));
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Equal("TargetUnavailable", (await unavailable.Content.ReadFromJsonAsync<RuntimeLogsUnavailable>())!.Reason);
        Assert.DoesNotContain("private-outage-canary", await unavailable.Content.ReadAsStringAsync());
        provider.Outage = false; provider.Block = false;
        using var response = await Client.GetAsync(Url(deployment)); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class TestProvider : IResourceDeploymentObserver, IResourceDeploymentDiagnosticsProvider
    {
        public ConcurrentQueue<TrackedStream> Sessions { get; } = new();
        public bool Replaced { get; set; }
        public bool Outage { get; set; }
        public bool Block { get; set; } = true;
        public Action? AtEof { get; set; }
        public bool StalledVerification { get; set; }
        public string Output { get; set; } = "controlled-runtime-marker\n";
        public Task<WorkloadObservation> ObserveAsync(ResourceDeploymentReconciliationSnapshot deployment, CancellationToken cancellationToken = default) => Task.FromResult<WorkloadObservation>(new UnavailableWorkloadObservation("Unsupported"));
        public Task<WorkloadDiagnostics> DiscoverAsync(ResourceDeploymentReconciliationSnapshot deployment, CancellationToken cancellationToken = default) => Task.FromResult<WorkloadDiagnostics>(new AvailableWorkloadDiagnostics([], false, [], false, null));
        public Task<RuntimeLogOpenResult> OpenLogsAsync(ResourceDeploymentReconciliationSnapshot deployment, RuntimeLogSelection selection, CancellationToken cancellationToken = default)
        {
            if (Outage) throw new HttpRequestException("private-outage-canary");
            if (selection.InstanceUid != "owned-uid") return Task.FromResult<RuntimeLogOpenResult>(new UnavailableRuntimeLog("InstanceRemoved"));
            if (selection.Container != "app") return Task.FromResult<RuntimeLogOpenResult>(new UnavailableRuntimeLog("ContainerUnavailable"));
            var stream = new TrackedStream(Output, Block, AtEof); Sessions.Enqueue(stream);
            return Task.FromResult<RuntimeLogOpenResult>(new OpenRuntimeLog(new RuntimeLogSource(stream, async ct =>
            {
                if (Outage) throw new HttpRequestException("private-outage-canary");
                if (StalledVerification) await Task.Delay(Timeout.Infinite, ct);
                return Replaced ? "InstanceReplaced" : null;
            })));
        }
    }
    private sealed class TrackedStream(string output, bool block, Action? atEof) : MemoryStream(Encoding.UTF8.GetBytes(output))
    {
        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length || !block)
            {
                var count = await base.ReadAsync(buffer, cancellationToken);
                if (count == 0) atEof?.Invoke();
                return count;
            }
            try
            {
                var pendingRead = Task.Delay(Timeout.Infinite, cancellationToken);
                ReadBlocked.TrySetResult();
                await pendingRead;
                return 0;
            }
            catch (OperationCanceledException) { ReadCanceled.TrySetResult(); throw; }
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); Disposed.TrySetResult(); }
    }
}
