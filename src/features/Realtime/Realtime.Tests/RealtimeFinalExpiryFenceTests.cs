using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Application.Endpoints;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Contracts;
using Xunit;

namespace Pole.Features.Realtime.Tests;

public sealed class RealtimeFinalExpiryFenceTests
{
    private const string Tenant = "aaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("expiry")]
    [InlineData("expiry-renewed")]
    [InlineData("logout")]
    [InlineData("ticket-loss")]
    [InlineData("cancel")]
    public async Task Publisher_rechecks_session_after_noncooperative_authority_await(string boundary)
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.SubscribeAsync();
        var pause = new Pause();
        scenario.BeforeMember = () => pause.WaitAsync();
        var pending = scenario.Publisher.PublishAsync(new("fake", Tenant), scenario.Cancellation.Token);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scenario.CrossBoundaryAsync(boundary);
        pause.Release.TrySetResult();
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await pending;
        Assert.Empty(scenario.Client.Notices);
        Assert.Equal(boundary == "logout" ? 1 : 0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("logout")]
    public async Task Publisher_rechecks_earlier_recipient_after_another_recipient_blocks(string boundary)
    {
        var scenario = await Scenario.CreateAsync();
        var first = new RealtimeSubscription("first", "a", "viewer", new("fake", Tenant), scenario.Clock.Now.AddMinutes(1));
        var otherPrincipal = IdentityFixture.User("other");
        await scenario.Tickets.StoreAsync(new(otherPrincipal, new() { ExpiresUtc = scenario.Clock.Now.AddMinutes(5) }, RealtimeFeature.CookieScheme));
        await scenario.Subscriptions.ReplaceAsync("a", [first], default);
        await scenario.Subscriptions.ReplaceAsync("b", [new("second", "b", "other", new("fake", Tenant), scenario.Clock.Now.AddMinutes(5))], default);
        var pause = new Pause();
        scenario.BeforeMemberForUser = user => user == "other" ? pause.WaitAsync() : Task.CompletedTask;
        var pending = scenario.Publisher.PublishAsync(new("fake", Tenant));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scenario.CrossBoundaryAsync(boundary);
        pause.Release.TrySetResult();
        await pending;
        Assert.DoesNotContain("a", scenario.Client.Notices);
        Assert.Contains("b", scenario.Client.Notices);
    }

    [Fact]
    public async Task Publisher_rechecks_changed_platform_ticket_roles_after_batch_storage_await()
    {
        var scenario = await Scenario.CreateAsync(admin: true);
        await scenario.Subscriptions.ReplaceAsync("a", [new("first", "a", "viewer", new("platform"), scenario.Deadline)], default);
        await scenario.Tickets.StoreAsync(new(IdentityFixture.User("other", admin: true), new() { ExpiresUtc = scenario.Deadline }, RealtimeFeature.CookieScheme));
        await scenario.Subscriptions.ReplaceAsync("b", [new("second", "b", "other", new("platform"), scenario.Deadline)], default);
        var pause = new Pause();
        scenario.Tickets.BeforeRetrieve = id => id == "other" ? pause.WaitAsync() : Task.CompletedTask;
        var pending = scenario.Publisher.PublishAsync(new("platform"));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scenario.Tickets.StoreAsync(new(IdentityFixture.User(), new() { ExpiresUtc = scenario.Deadline }, RealtimeFeature.CookieScheme));
        scenario.Tickets.BeforeRetrieve = null;
        pause.Release.TrySetResult();
        await pending;
        Assert.DoesNotContain("a", scenario.Client.Notices);
        Assert.Contains("b", scenario.Client.Notices);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("issuer")]
    [InlineData("session")]
    [InlineData("anonymous")]
    public async Task Publisher_cannot_adopt_a_replaced_identity_under_the_original_session_key(string field)
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.SubscribeAsync();
        var pause = new Pause();
        scenario.BeforeMember = () => pause.WaitAsync();
        var pending = scenario.Publisher.PublishAsync(new("fake", Tenant));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var claims = new[] { new Claim("sub", field == "subject" ? "other" : "viewer"),
            new Claim("iss", field == "issuer" ? "https://other.test" : "https://issuer.test"),
            new Claim(RealtimeFeature.SessionClaim, field == "session" ? "other" : "viewer") };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, field == "anonymous" ? null : RealtimeFeature.CookieScheme));
        await scenario.Tickets.RenewAsync("viewer", new(principal, new() { ExpiresUtc = scenario.Deadline }, RealtimeFeature.CookieScheme));
        pause.Release.TrySetResult();
        await pending;
        Assert.Empty(scenario.Client.Notices);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
    }

    [Fact]
    public async Task Publisher_drops_same_connection_when_an_earlier_interest_expires_during_later_final_authority_work()
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.Tickets.RenewAsync("viewer", new(IdentityFixture.User(), new() { ExpiresUtc = scenario.Clock.Now.AddMinutes(5) }, RealtimeFeature.CookieScheme));
        await scenario.Subscriptions.ReplaceAsync("connection", [new("first", "connection", "viewer", new("fake", Tenant), scenario.Deadline),
            new("second", "connection", "viewer", new("fake", Tenant, "resource"), scenario.Clock.Now.AddMinutes(5))], default);
        var pause = new Pause();
        var calls = 0;
        scenario.BeforeMember = () => ++calls == 4 ? pause.WaitAsync() : Task.CompletedTask;
        var pending = scenario.Publisher.PublishAsync(new("fake", Tenant));
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.Clock.Now = scenario.Deadline.AddSeconds(1);
        pause.Release.TrySetResult();
        await pending;
        Assert.Empty(scenario.Client.Notices);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
    }

    [Fact]
    public async Task Publisher_retains_a_shorter_deadline_observed_before_another_recipient_waits()
    {
        var scenario = await Scenario.CreateAsync();
        var longExpiry = scenario.Clock.Now.AddMinutes(5);
        await scenario.Tickets.RenewAsync("viewer", new(IdentityFixture.User(), new() { ExpiresUtc = longExpiry }, RealtimeFeature.CookieScheme));
        await scenario.Tickets.StoreAsync(new(IdentityFixture.User("other"), new() { ExpiresUtc = longExpiry }, RealtimeFeature.CookieScheme));
        await scenario.Subscriptions.ReplaceAsync("a", [new("first", "a", "viewer", new("fake", Tenant), longExpiry)], default);
        await scenario.Subscriptions.ReplaceAsync("b", [new("second", "b", "other", new("fake", Tenant), longExpiry)], default);
        var first = new Pause();
        var second = new Pause();
        scenario.BeforeMemberForUser = user => user == "viewer" ? first.WaitAsync() : second.WaitAsync();
        var pending = scenario.Publisher.PublishAsync(new("fake", Tenant));
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scenario.Tickets.RenewAsync("viewer", new(IdentityFixture.User(), new() { ExpiresUtc = scenario.Clock.Now.AddSeconds(10) }, RealtimeFeature.CookieScheme));
        first.Release.TrySetResult();
        await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.Clock.Now = scenario.Clock.Now.AddSeconds(11);
        await scenario.Tickets.RenewAsync("viewer", new(IdentityFixture.User(), new() { ExpiresUtc = longExpiry }, RealtimeFeature.CookieScheme));
        second.Release.TrySetResult();
        await pending;
        Assert.DoesNotContain("a", scenario.Client.Notices);
        Assert.Contains("b", scenario.Client.Notices);
        Assert.Equal(0, scenario.Client.SessionRevocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_registry_wait_cannot_preserve_stale_platform_authority(bool heartbeat)
    {
        var scenario = await Scenario.CreateAsync(admin: true);
        if (heartbeat) Assert.True(Assert.Single(await scenario.Hub.Subscribe([new("platform")])).Covered);
        var pause = new Pause();
        scenario.Subscriptions.AfterReplace = () => pause.WaitAsync();
        var pending = heartbeat ? scenario.Hub.Heartbeat() : scenario.Hub.Subscribe([new("platform")]);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scenario.Tickets.RenewAsync("viewer", new(IdentityFixture.User(), new() { ExpiresUtc = scenario.Deadline }, RealtimeFeature.CookieScheme));
        pause.Release.TrySetResult();
        Assert.Empty(await pending);
        Assert.Equal([null], scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Subscriptions.Inner.Connections.Values.SelectMany(value => value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_registry_wait_rechecks_current_tenant_membership(bool heartbeat)
    {
        var scenario = await Scenario.CreateAsync();
        if (heartbeat) await scenario.SubscribeAsync();
        var pause = new Pause();
        scenario.Subscriptions.AfterReplace = () => pause.WaitAsync();
        var pending = heartbeat ? scenario.Hub.Heartbeat() : scenario.Hub.Subscribe([new("fake", Tenant)]);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.MemberAllowed = false;
        pause.Release.TrySetResult();
        Assert.Empty(await pending);
        Assert.Equal([Tenant], scenario.Client.AccessRevocations);
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Empty(scenario.Subscriptions.Inner.Connections.Values.SelectMany(value => value));
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("logout")]
    [InlineData("cancel")]
    public async Task Subscribe_cannot_acknowledge_after_noncooperative_provider_work(string boundary)
    {
        var scenario = await Scenario.CreateAsync();
        var pause = new Pause();
        scenario.Owner.AfterSubscribe = () => pause.WaitAsync();
        var pending = scenario.Hub.Subscribe([new("fake", Tenant)]);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scenario.CrossBoundaryAsync(boundary);
        pause.Release.TrySetResult();
        await AssertRejectedAsync(scenario, pending, boundary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnected_invocation_cleans_a_noncooperative_provider_write_completed_after_disconnect(bool heartbeat)
    {
        var scenario = await Scenario.CreateAsync();
        var pause = new Pause();
        if (heartbeat)
        {
            await scenario.SubscribeAsync();
            scenario.Owner.BeforeRenew = () => pause.WaitAsync();
        }
        else scenario.Owner.BeforeSubscribe = () => pause.WaitAsync();
        var pending = heartbeat ? scenario.Hub.Heartbeat() : scenario.Hub.Subscribe([new("fake", Tenant)]);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scenario.Cancellation.Cancel();
        await scenario.Hub.OnDisconnectedAsync(null);
        pause.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Empty(scenario.Subscriptions.Inner.Connections.Values.SelectMany(value => value));
        Assert.Equal(0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
    }

    [Theory]
    [InlineData(false, "expiry")]
    [InlineData(false, "logout")]
    [InlineData(true, "expiry")]
    [InlineData(true, "expiry-renewed")]
    [InlineData(true, "logout")]
    [InlineData(true, "cancel")]
    public async Task Positive_acknowledgement_cannot_cross_final_registry_await(bool heartbeat, string boundary)
    {
        var scenario = await Scenario.CreateAsync();
        if (heartbeat) await scenario.SubscribeAsync();
        var pause = new Pause();
        // The registry already committed before its response is delayed. A concurrent logout
        // therefore cannot be caught by the existing pre-commit Redis marker check.
        scenario.Subscriptions.AfterReplace = () => pause.WaitAsync();
        var pending = heartbeat ? scenario.Hub.Heartbeat() : scenario.Hub.Subscribe([new("fake", Tenant)]);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scenario.CrossBoundaryAsync(boundary);
        pause.Release.TrySetResult();
        await AssertRejectedAsync(scenario, pending, boundary);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("logout")]
    public async Task Heartbeat_cannot_acknowledge_after_its_final_authority_await(string boundary)
    {
        var scenario = await Scenario.CreateAsync();
        await scenario.SubscribeAsync();
        var pause = new Pause();
        var calls = 0;
        scenario.BeforeMember = () => ++calls == 2 ? pause.WaitAsync() : Task.CompletedTask;
        var pending = scenario.Hub.Heartbeat();
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scenario.CrossBoundaryAsync(boundary);
        pause.Release.TrySetResult();
        await AssertRejectedAsync(scenario, pending, boundary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootstrap_rejects_original_deadline_or_cancellation_after_authentication_without_signin(bool cancelled)
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var pause = new Pause();
        var authentication = new ControlledAuthentication(pause);
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "viewer"), new Claim("iss", "https://issuer.test"), new Claim("exp", clock.Now.AddSeconds(10).ToUnixTimeSeconds().ToString())], "Bearer"))
        };
        var handler = new CreateRealtimeSessionHandler(new HttpContextAccessor { HttpContext = context }, new(), clock);
        var pending = handler.HandleAsync(new(), cancellation.Token);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelled) cancellation.Cancel(); else clock.Now = clock.Now.AddSeconds(11);
        pause.Release.TrySetResult();
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.IsType<RealtimeSessionIdentityUnavailable>((await pending).Value);
        Assert.Equal(0, authentication.SignIns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootstrap_cannot_return_created_after_ticket_persistence_crosses_deadline_or_cancellation(bool cancelled)
    {
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var authentication = new ControlledAuthentication(new Pause()) { AuthenticationResult = AuthenticateResult.NoResult() };
        var pause = new Pause();
        authentication.AfterSignIn = () => pause.WaitAsync();
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "viewer"), new Claim("iss", "https://issuer.test"), new Claim("exp", clock.Now.AddSeconds(10).ToUnixTimeSeconds().ToString())], "Bearer"))
        };
        var handler = new CreateRealtimeSessionHandler(new HttpContextAccessor { HttpContext = context }, new(), clock);
        var pending = handler.HandleAsync(new(), cancellation.Token);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelled) cancellation.Cancel(); else clock.Now = clock.Now.AddSeconds(11);
        pause.Release.TrySetResult();
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else Assert.IsType<RealtimeSessionIdentityUnavailable>((await pending).Value);
        Assert.Equal(1, authentication.SignIns);
        Assert.Equal(1, authentication.SignOuts);
    }

    private static async Task AssertRejectedAsync(Scenario scenario, Task<IReadOnlyList<RealtimeSubscriptionAcknowledgement>> pending, string boundary)
    {
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else if (boundary == "logout") Assert.Empty(await pending);
        else if (boundary == "expiry-renewed") Assert.Empty(await pending);
        else await Assert.ThrowsAsync<HubException>(() => pending);
        Assert.Empty(scenario.Owner.Interests);
        Assert.Empty(scenario.Subscriptions.Inner.Connections.Values.SelectMany(value => value));
        Assert.Equal(boundary == "logout" ? 1 : 0, scenario.Client.SessionRevocations);
        Assert.Empty(scenario.Client.AccessRevocations);
    }

    private sealed class Scenario
    {
        public Clock Clock { get; } = new();
        public ControlledTickets Tickets { get; } = new();
        public ControlledSubscriptions Subscriptions { get; } = new();
        public Owner Owner { get; } = new();
        public RecordingClient Client { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTimeOffset Deadline => Clock.Initial.AddMinutes(1);
        public RealtimeHub Hub { get; private set; } = null!;
        public RealtimePublisher Publisher { get; private set; } = null!;
        public bool MemberAllowed { get; set; } = true;
        public Func<Task>? BeforeMember { get; set; }
        public Func<string, Task>? BeforeMemberForUser { get; set; }
        public static async Task<Scenario> CreateAsync(bool admin = false)
        {
            var value = new Scenario();
            var principal = IdentityFixture.User(admin: admin);
            await value.Tickets.StoreAsync(new(principal, new() { ExpiresUtc = value.Deadline }, RealtimeFeature.CookieScheme));
            var registry = new RealtimeTopicRegistry([new("fake", RealtimeScope.Tenant), new("platform", RealtimeScope.Platform)], new());
            var access = new RealtimeAccess(async (tenant, user, _, _) =>
            {
                if (value.BeforeMember is { } before) await before();
                if (value.BeforeMemberForUser is { } beforeUser) await beforeUser(user);
                return value.MemberAllowed ? new TenantMemberSnapshot(tenant, user, TenantMembershipRole.Viewer) : new TenantMemberMissing(tenant, user);
            }, (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentitySnapshot(tenant, "name")), registry);
            var clients = new RecordingClients(value.Client);
            value.Hub = new(value.Subscriptions, value.Tickets, access, [value.Owner], value.Clock)
            { Context = new Caller(principal, value.Cancellation.Token), Clients = clients };
            value.Publisher = new(value.Subscriptions, value.Tickets, access, registry, new HubContext(clients), value.Clock);
            return value;
        }
        public async Task SubscribeAsync() => Assert.True(Assert.Single(await Hub.Subscribe([new("fake", Tenant)])).Covered);
        public async Task CrossBoundaryAsync(string boundary)
        {
            if (boundary is "expiry" or "expiry-renewed") Clock.Now = Deadline.AddSeconds(1);
            if (boundary == "expiry-renewed") await Tickets.StoreAsync(new(IdentityFixture.User(), new() { ExpiresUtc = Clock.Now.AddMinutes(5) }, RealtimeFeature.CookieScheme));
            if (boundary is "logout" or "ticket-loss") await Tickets.RemoveAsync("viewer");
            if (boundary == "logout") await Subscriptions.RevokeSessionAsync("viewer", default);
            if (boundary == "cancel") Cancellation.Cancel();
        }
    }
    private sealed class Pause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitAsync() { Entered.TrySetResult(); return Release.Task; } // Deliberately ignores cancellation.
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Initial { get; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class ControlledTickets : IRealtimeTicketStore
    {
        private readonly MemoryTickets inner = new();
        public Func<string, Task>? BeforeRetrieve { get; set; }
        public Task<string> StoreAsync(AuthenticationTicket ticket) => inner.StoreAsync(ticket);
        public Task RenewAsync(string key, AuthenticationTicket ticket) => inner.RenewAsync(key, ticket);
        public async Task<AuthenticationTicket?> RetrieveAsync(string key) { if (BeforeRetrieve is { } before) await before(key); return await inner.RetrieveAsync(key); }
        public Task RemoveAsync(string key) => inner.RemoveAsync(key);
    }
    private sealed class ControlledSubscriptions : IRealtimeSubscriptions
    {
        public MemorySubscriptions Inner { get; } = new();
        public Func<Task>? AfterReplace { get; set; }
        public async Task ReplaceAsync(string connection, IReadOnlyList<RealtimeSubscription> values, CancellationToken ct)
        { await Inner.ReplaceAsync(connection, values, ct); if (AfterReplace is { } after) await after(); }
        public Task RemoveAsync(string connection, IReadOnlyCollection<string>? ids, CancellationToken ct) => Inner.RemoveAsync(connection, ids, ct);
        public Task<IReadOnlyList<RealtimeSubscription>> FindAsync(string topic, string? tenant, CancellationToken ct) => Inner.FindAsync(topic, tenant, ct);
        public Task<IReadOnlyList<RealtimeSubscription>> FindAllAsync(string topic, CancellationToken ct) => Inner.FindAllAsync(topic, ct);
        public Task<IReadOnlyList<string>> RevokeSessionAsync(string session, CancellationToken ct) => Inner.RevokeSessionAsync(session, ct);
        public Task<bool> IsSessionRevokedAsync(string session, CancellationToken ct) => Inner.IsSessionRevokedAsync(session, ct);
    }
    private sealed class Owner : IRealtimeTopicLifecycle
    {
        public string Topic => "fake";
        public HashSet<string> Interests { get; } = [];
        public Func<Task>? BeforeSubscribe { get; set; }
        public Func<Task>? BeforeRenew { get; set; }
        public Func<Task>? AfterSubscribe { get; set; }
        public async Task<bool> SubscribeAsync(RealtimeSubscription value, ClaimsPrincipal principal, CancellationToken ct)
        { if (BeforeSubscribe is { } before) await before(); Interests.Add(value.Id); if (AfterSubscribe is { } after) await after(); return true; }
        public async Task<bool> RenewAsync(RealtimeSubscription value, ClaimsPrincipal principal, CancellationToken ct)
        { if (BeforeRenew is { } before) { await before(); Interests.Add(value.Id); } return true; }
        public Task UnsubscribeAsync(RealtimeSubscription value, CancellationToken ct) { Interests.Remove(value.Id); return Task.CompletedTask; }
    }
    private sealed class Caller(
        ClaimsPrincipal principal,
        CancellationToken cancellation) : HubCallerContext
    {
        public override string ConnectionId => "connection";
        public override string UserIdentifier => "viewer";
        public override ClaimsPrincipal User => principal;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => cancellation;
        public override void Abort() { }
    }
    private sealed class RecordingClient : IRealtimeClient
    {
        public List<string> Notices { get; } = [];
        public int SessionRevocations { get; private set; }
        public List<string?> AccessRevocations { get; } = [];
        public Task Changed(RealtimeChange change) { Notices.Add("connection"); return Task.CompletedTask; }
        public Task SessionRevoked() { SessionRevocations++; return Task.CompletedTask; }
        public Task AccessRevoked(string? tenant) { AccessRevocations.Add(tenant); return Task.CompletedTask; }
        public IRealtimeClient For(IEnumerable<string> connections) => new Receiver(this, connections.ToArray());
        private sealed class Receiver(RecordingClient parent, string[] connections) : IRealtimeClient
        {
            public Task Changed(RealtimeChange change) { parent.Notices.AddRange(connections); return Task.CompletedTask; }
            public Task SessionRevoked() => parent.SessionRevoked();
            public Task AccessRevoked(string? tenant) => parent.AccessRevoked(tenant);
        }
    }
    private sealed class RecordingClients(RecordingClient client) : IHubCallerClients<IRealtimeClient>
    {
        public IRealtimeClient Caller => client;
        public IRealtimeClient Others => client;
        public IRealtimeClient OthersInGroup(string name) => client;
        public IRealtimeClient All => client;
        public IRealtimeClient AllExcept(IReadOnlyList<string> ids) => client;
        public IRealtimeClient Client(string id) => client.For([id]);
        public IRealtimeClient Clients(IReadOnlyList<string> ids) => client.For(ids);
        public IRealtimeClient Group(string name) => client;
        public IRealtimeClient GroupExcept(string name, IReadOnlyList<string> ids) => client;
        public IRealtimeClient Groups(IReadOnlyList<string> names) => client;
        public IRealtimeClient User(string id) => client;
        public IRealtimeClient Users(IReadOnlyList<string> ids) => client;
    }
    private sealed class HubContext(RecordingClients clients) : IHubContext<RealtimeHub, IRealtimeClient>
    {
        public IHubClients<IRealtimeClient> Clients => clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }
    private sealed class ControlledAuthentication(Pause pause) : IAuthenticationService
    {
        public int SignIns { get; private set; }
        public int SignOuts { get; private set; }
        public AuthenticateResult? AuthenticationResult { get; init; }
        public Func<Task>? AfterSignIn { get; set; }
        public async Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) { if (AuthenticationResult is { } result) return result; await pause.WaitAsync(); return AuthenticateResult.NoResult(); }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public async Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) { SignIns++; if (AfterSignIn is { } after) await after(); }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) { SignOuts++; return Task.CompletedTask; }
    }
}
