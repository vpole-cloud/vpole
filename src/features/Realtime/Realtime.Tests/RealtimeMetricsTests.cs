using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Contracts;
using Xunit;

namespace Pole.Features.Realtime.Tests;

public sealed class RealtimeMetricsTests
{
    [Fact]
    public async Task DeliveryAndLifecycleMeasurementsContainOnlyBoundedOperationalLabels()
    {
        using var meter = new Meter(RealtimeMetrics.MeterName);
        using var capture = new MetricCapture(meter);
        using var metrics = new RealtimeMetrics(meter);
        var clock = new Clock();
        var registry = new RealtimeTopicRegistry([new("registered", RealtimeScope.User)], new());
        var access = Access(registry);
        var tickets = new MemoryTickets();
        await tickets.StoreAsync(Ticket(clock));
        var subscriptions = new MemorySubscriptions();
        await subscriptions.ReplaceAsync("connection-private",
            [
                new("subscription-private",
                    "connection-private",
                    "viewer",
                    new("registered", ResourceId: "viewer"),
                    clock.Now.AddMinutes(1))
            ],
            default);
        var client = new Client();
        var publisher = new RealtimePublisher(subscriptions,
            tickets,
            access,
            registry,
            new HubContext(client),
            clock,
            metrics);
        await publisher.PublishAsync(new("registered", ResourceId: "viewer"));
        await publisher.PublishAsync(new("unregistered-secret-canary", ResourceId: "resource-secret-canary"));
        var hub = new RealtimeHub(subscriptions, tickets, access, [], clock, metrics)
        {
            Context = new Context(),
            Clients = new RecordingClients(client)
        };
        await hub.OnConnectedAsync();
        await hub.Subscribe([new("registered", ResourceId: "viewer"), new("unregistered-secret-canary")]);
        var beforeHeartbeat = capture.Sum("pole.realtime.coverage.transitions");
        await hub.Heartbeat();
        Assert.Equal(beforeHeartbeat, capture.Sum("pole.realtime.coverage.transitions"));
        Assert.True(await hub.ReportClientLifecycle(new(2, 3)));
        Assert.False(await hub.ReportClientLifecycle(new(100, 100)));
        clock.Now += TimeSpan.FromSeconds(10);
        Assert.True(await hub.ReportClientLifecycle(new(1, 0)));
        await hub.Unsubscribe();
        await hub.OnDisconnectedAsync(null);
        Assert.Equal(2, capture.Sum("pole.realtime.delivery.attempts"));
        Assert.Equal(1, capture.Sum("pole.realtime.delivery.recipients"));
        Assert.Equal(3, capture.Sum("pole.realtime.reconnects"));
        Assert.Equal(4, capture.Sum("pole.realtime.fallbacks"));
        Assert.Equal(2, capture.Sum("pole.realtime.connections.transitions"));
        Assert.Contains(capture.Measurements,
            item => item.Name == "pole.realtime.delivery.duration" && item.Value >= 0);
        Assert.All(capture.Measurements.SelectMany(item => item.Tags),
            tag =>
            {
                Assert.Contains(tag.Key, new[] { "topic", "scope", "outcome", "source" });
                Assert.Contains(tag.Value?.ToString(),
                    new[]
                    {
                        "registered", "unknown", "user", "sent", "unavailable", "connected", "disconnected",
                        "subscribed",
                        "released", "covered", "uncovered", "server", "client"
                    });
            });
        Assert.DoesNotContain(capture.Measurements.SelectMany(item => item.Tags),
            tag => tag.Value?.ToString()?.Contains("private", StringComparison.Ordinal) == true ||
                   tag.Value?.ToString()?.Contains("canary", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("no_recipients")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task DeliveryOutcomePreservesTheNormalErrorPathAndAuthorizedRecipientCount(string expected)
    {
        using var meter = new Meter(RealtimeMetrics.MeterName);
        using var capture = new MetricCapture(meter);
        using var metrics = new RealtimeMetrics(meter);
        var clock = new Clock();
        var registry = new RealtimeTopicRegistry([new("registered", RealtimeScope.User)], new());
        var tickets = new MemoryTickets();
        await tickets.StoreAsync(Ticket(clock));
        var subscriptions = new MemorySubscriptions();
        if (expected != "no_recipients")
            await subscriptions.ReplaceAsync("connection-private",
                [
                    new("subscription-private",
                        "connection-private",
                        "viewer",
                        new("registered", ResourceId: "viewer"),
                        clock.Now.AddMinutes(1))
                ],
                default);
        var failure = new IOException("private-send-message");
        var client = new Client { SendFailure = expected == "failed" ? failure : null };
        var publisher = new RealtimePublisher(subscriptions,
            tickets,
            Access(registry),
            registry,
            new HubContext(client),
            clock,
            metrics);
        using var lifetime = new CancellationTokenSource();
        if (expected == "cancelled")
            lifetime.Cancel();
        if (expected == "failed")
            Assert.Same(failure,
                await Assert.ThrowsAsync<IOException>(() =>
                    publisher.PublishAsync(new("registered", ResourceId: "viewer"), lifetime.Token)));
        else if (expected == "cancelled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                publisher.PublishAsync(new("registered", ResourceId: "viewer"), lifetime.Token));
        else
            await publisher.PublishAsync(new("registered", ResourceId: "viewer"), lifetime.Token);
        Assert.Equal(1, capture.Sum("pole.realtime.delivery.attempts"));
        Assert.Equal(expected == "failed" ? 1 : 0, capture.Sum("pole.realtime.delivery.recipients"));
        Assert.All(capture.Measurements.SelectMany(item => item.Tags),
            tag =>
            {
                Assert.Contains(tag.Key, new[] { "topic", "scope", "outcome" });
                Assert.Contains(tag.Value?.ToString(), new[] { "registered", "user", expected });
            });
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(101, 0)]
    [InlineData(0, 101)]
    [InlineData(0, 0)]
    public async Task InvalidClientReportNeverReadsAuthorityOrChangesSubscriptions(int reconnects, int fallbacks)
    {
        using var metrics = new RealtimeMetrics();
        var tickets = new MemoryTickets
        {
            RetrieveFailure = new InvalidOperationException("Must not read authority for invalid shape")
        };
        var subscriptions = new MemorySubscriptions();
        var hub = new RealtimeHub(subscriptions, tickets, Access(new([], new())), [], new Clock(), metrics)
        {
            Context = new Context()
        };
        await Assert.ThrowsAsync<HubException>(() => hub.ReportClientLifecycle(new(reconnects, fallbacks)));
        Assert.Equal(0, subscriptions.ReplaceCalls);
        Assert.Equal(0, subscriptions.RemoveCalls);
        Assert.Empty(subscriptions.RevocationChecks);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("storage-failure")]
    public async Task UnavailableReportsDoNotRecordClientCountersOrRevokeWorkspaceInterests(string reason)
    {
        using var meter = new Meter(RealtimeMetrics.MeterName);
        using var capture = new MetricCapture(meter);
        using var metrics = new RealtimeMetrics(meter);
        var clock = new Clock();
        var tickets = new MemoryTickets();
        var subscriptions = new MemorySubscriptions();
        var ticket = Ticket(clock);
        if (reason == "expired")
            ticket.Properties.ExpiresUtc = clock.Now;
        if (reason == "foreign")
            ticket = new(IdentityFixture.User("other"), ticket.Properties, RealtimeFeature.CookieScheme);
        if (reason != "missing")
            tickets.Tickets["viewer"] = ticket;
        if (reason == "revoked")
            subscriptions.Revoked.Add("viewer");
        if (reason == "storage-failure")
            tickets.RetrieveFailure = new IOException("private-infrastructure-message");
        var hub = new RealtimeHub(subscriptions, tickets, Access(new([], new())), [], clock, metrics)
        {
            Context = new Context()
        };
        if (reason == "storage-failure")
            await Assert.ThrowsAsync<IOException>(() => hub.ReportClientLifecycle(new(1, 1)));
        else
            await Assert.ThrowsAsync<HubException>(() => hub.ReportClientLifecycle(new(1, 1)));
        Assert.Equal(0, capture.Sum("pole.realtime.reconnects"));
        Assert.Equal(0, capture.Sum("pole.realtime.fallbacks"));
        Assert.Equal(0, subscriptions.ReplaceCalls);
        Assert.Equal(0, subscriptions.RemoveCalls);
    }

    [Fact]
    public async Task RevocationIsRecheckedEvenWhenAReportWouldBeRateLimitedAndExpiryDuringCheckIsFenced()
    {
        using var metrics = new RealtimeMetrics();
        var clock = new Clock();
        var tickets = new MemoryTickets();
        var subscriptions = new MemorySubscriptions();
        await tickets.StoreAsync(Ticket(clock));
        var hub = new RealtimeHub(subscriptions, tickets, Access(new([], new())), [], clock, metrics)
        {
            Context = new Context()
        };
        Assert.True(await hub.ReportClientLifecycle(new(1, 1)));
        subscriptions.Revoked.Add("viewer");
        await Assert.ThrowsAsync<HubException>(() => hub.ReportClientLifecycle(new(1, 1)));
        Assert.Equal(2, subscriptions.RevocationChecks.Count);
        Assert.Equal(0, subscriptions.RemoveCalls);
        subscriptions.Revoked.Clear();
        subscriptions.OnRevocationCheck = () => clock.Now += TimeSpan.FromMinutes(1);
        await Assert.ThrowsAsync<HubException>(() => hub.ReportClientLifecycle(new(1, 1)));
        Assert.Equal(0, subscriptions.RemoveCalls);
    }

    private static RealtimeAccess Access(RealtimeTopicRegistry registry) => new((request, _, _) =>
            throw new InvalidOperationException("Metrics cannot authorize a tenant."),
        (_, _) => throw new InvalidOperationException("Metrics cannot load a tenant."),
        registry);

    private static AuthenticationTicket Ticket(Clock clock) => new(IdentityFixture.User(),
        new()
        {
            ExpiresUtc = clock.Now.AddMinutes(1)
        },
        RealtimeFeature.CookieScheme);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Context : HubCallerContext
    {
        public override string ConnectionId => "connection-private";
        public override string UserIdentifier => "viewer";
        public override ClaimsPrincipal User => IdentityFixture.User();

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    private sealed class Client : IRealtimeClient
    {
        public Exception? SendFailure { get; init; }

        public Task Changed(RealtimeChange change) =>
            SendFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;

        public Task SessionRevoked() =>
            throw new InvalidOperationException("Diagnostic reports cannot revoke a workspace.");

        public Task AccessRevoked(string? tenantId) =>
            throw new InvalidOperationException("Diagnostic reports cannot revoke access.");
    }

    private sealed class HubContext(Client client) : IHubContext<RealtimeHub, IRealtimeClient>
    {
        public IHubClients<IRealtimeClient> Clients { get; } = new RecordingClients(client);

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingClients(Client client) : IHubCallerClients<IRealtimeClient>
    {
        public IRealtimeClient Caller => client;
        public IRealtimeClient Others => client;
        public IRealtimeClient OthersInGroup(string groupName) => client;
        public IRealtimeClient All => client;
        public IRealtimeClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => client;
        public IRealtimeClient Client(string connectionId) => client;
        public IRealtimeClient Clients(IReadOnlyList<string> connectionIds) => client;
        public IRealtimeClient Group(string groupName) => client;
        public IRealtimeClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => client;
        public IRealtimeClient Groups(IReadOnlyList<string> groupNames) => client;
        public IRealtimeClient User(string userId) => client;
        public IRealtimeClient Users(IReadOnlyList<string> userIds) => client;
    }

    private sealed record Measurement(
        string Name,
        double Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed class MetricCapture : IDisposable
    {
        public ConcurrentQueue<Measurement> Measurements { get; } = new();

        private readonly MeterListener listener = new();

        public MetricCapture(Meter meter)
        {
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    owner.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Enqueue(new(instrument.Name, value, tags.ToArray())));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Measurements.Enqueue(new(instrument.Name, value, tags.ToArray())));
            listener.Start();
        }

        public double Sum(string name) => Measurements.Where(item => item.Name == name).Sum(item => item.Value);
        public void Dispose() => listener.Dispose();
    }
}
