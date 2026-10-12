using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Application.Endpoints;
using Pole.Features.Realtime.Application.Redis;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Mediator;
using Pole.Testing;
using StackExchange.Redis;
using Xunit;

namespace Pole.Tests.Integration.Realtime;

public sealed class RealtimeRedisFixture : IAsyncLifetime
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(RepositoryPaths.FindRoot(), ".artifacts", "realtime", "tests", Guid.NewGuid().ToString("N")));
    public IContainer Container { get; private set; } = null!;
    public string CertificatePath => Path.Combine(_directory, "key-protection.pfx");
    public string ConnectionString => Container.Hostname + ":" + Container.GetMappedPublicPort(6379);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Pole realtime test keys", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        await File.WriteAllBytesAsync(CertificatePath, certificate.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(CertificatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Container = new ContainerBuilder("redis:7.4-alpine")
            .WithTestOwnership(TestRunIdentity.Create())
            .WithPortBinding(6379, true)
            .WithCommand("--appendonly", "yes", "--appendfsync", "always", "--maxmemory-policy", "noeviction")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();
        await Container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (Container is not null) await Container.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    public IConfiguration Configuration(string? topic = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Realtime:Enabled"] = "true",
        ["Realtime:AllowedOrigins:0"] = "https://frontend.test",
        ["Realtime:EnabledTopics:0"] = topic ?? "test",
        ["ConnectionStrings:realtime"] = ConnectionString,
        ["Realtime:KeyProtectionCertificatePath"] = CertificatePath
    }).Build();

    public ServiceProvider Services(TimeProvider? clock = null)
    {
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        if (clock is not null) services.AddSingleton(clock);
        services.AddRealtimeFeature(configuration, development: true);
        services.AddRealtimeRedis(configuration, development: true);
        return services.BuildServiceProvider(validateScopes: true);
    }
}

public sealed class RealtimeRedisTests(RealtimeRedisFixture fixture) : IClassFixture<RealtimeRedisFixture>
{
    [Fact]
    public async Task MixedTopicsAcknowledgeAvailabilityWithoutRegisteringUnavailableInterests()
    {
        var topic = "available-" + Guid.NewGuid().ToString("N");
        var disabled = "disabled-" + Guid.NewGuid().ToString("N");
        var unknown = "unknown-" + Guid.NewGuid().ToString("N");
        await using var server = await StartServerAsync(topic, disabled);
        var cookies = new CookieContainer();
        using var bootstrap = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
        bootstrap.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
        bootstrap.DefaultRequestHeaders.Authorization = new("Bearer", "realtime-test-credential");
        using var response = await bootstrap.PostAsync(Address(server) + RealtimeFeature.Route + "/session", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var client = Client(Address(server), cookies);
        var changes = new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionRevoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revocations = 0;
        var sessionRevocations = 0;
        client.On<RealtimeChange>("Changed", change => changes.TrySetResult(change));
        client.On<string?>("AccessRevoked", _ => Interlocked.Increment(ref revocations));
        client.On("SessionRevoked", () => { Interlocked.Increment(ref sessionRevocations); sessionRevoked.TrySetResult(); });
        await client.StartAsync();
        var initial = await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
            [new RealtimeSubscriptionRequest[] { new(topic), new(disabled), new(unknown, "unsupported-scope") }]);
        Assert.Equal([true, false, false], initial.Select(value => value.Covered));
        var renewed = await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", []);
        Assert.Equal(initial, renewed);
        var registry = server.Services.GetRequiredService<IRealtimeSubscriptions>();
        var active = Assert.Single(await registry.FindAsync(topic, null, default));
        Assert.Empty(await registry.FindAsync(disabled, null, default));
        Assert.Empty(await registry.FindAsync(unknown, "unsupported-scope", default));
        using var scope = server.Services.CreateScope();
        var publish = scope.ServiceProvider.GetRequiredService<PublishRealtimeChange>();
        await publish(new(disabled, Revision: 99));
        await publish(new(unknown, "unsupported-scope", Revision: 99));
        await publish(new(topic, Revision: 1));
        Assert.Equal(1, (await changes.Task.WaitAsync(TimeSpan.FromSeconds(15))).Revision);
        Assert.Equal(0, Volatile.Read(ref revocations));
        await client.InvokeCoreAsync("Unsubscribe", []);
        Assert.Empty(await registry.FindAsync(topic, null, default));
        Assert.Empty(await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", []));
        var unavailable = await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
            [new RealtimeSubscriptionRequest[] { new(disabled), new(unknown) }]);
        Assert.All(unavailable, value => Assert.False(value.Covered));
        await server.Services.GetRequiredService<IRealtimeTicketStore>().RemoveAsync(active.SessionId);
        await Assert.ThrowsAsync<HubException>(() => client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", []));
        Assert.Equal(HubConnectionState.Connected, client.State);
        Assert.Equal(0, Volatile.Read(ref sessionRevocations));
        Assert.Equal(0, Volatile.Read(ref revocations));
        await registry.RevokeSessionAsync(active.SessionId, default);
        Assert.Empty(await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", []));
        await sessionRevoked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, Volatile.Read(ref sessionRevocations));
    }

    [Fact]
    public async Task ProtectedTicketsAreSharedAllowlistedExpiringAndCannotBeResurrected()
    {
        var clock = new AdjustableClock();
        await using var first = fixture.Services(clock);
        await using var second = fixture.Services(clock);
        var firstTickets = first.GetRequiredService<IRealtimeTicketStore>();
        var secondTickets = second.GetRequiredService<IRealtimeTicketStore>();
        var session = Guid.NewGuid().ToString("N");
        var original = Ticket(session, clock.GetUtcNow().AddMinutes(10));
        var properties = original.Properties;
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "raw-bearer-secret" }]);
        properties.Items["private"] = "raw-private-value";
        var key = await firstTickets.StoreAsync(original);
        Assert.Equal(session, key);
        var restored = await secondTickets.RetrieveAsync(key);
        Assert.NotNull(restored);
        Assert.InRange(restored.Properties.ExpiresUtc!.Value,
            clock.GetUtcNow().AddMinutes(5).AddSeconds(-1), clock.GetUtcNow().AddMinutes(5));
        Assert.Empty(restored.Properties.GetTokens());
        Assert.DoesNotContain("private", restored.Properties.Items.Keys);
        Assert.All(restored.Principal.Claims, claim => Assert.Contains(claim.Type,
            new[] { "iss", "sub", "sid", "roles", RealtimeFeature.SessionClaim }));
        var database = first.GetRequiredService<RealtimeRedisConnection>().Database;
        var raw = await database.StringGetAsync("{pole:realtime}:ticket:" + session);
        Assert.DoesNotContain("raw-bearer-secret", Encoding.UTF8.GetString((byte[])raw!));
        var keyring = await database.ListRangeAsync("{pole:realtime}:data-protection");
        Assert.NotEmpty(keyring);
        Assert.All(keyring, item => Assert.True(item.ToString().Contains("encryptedSecret", StringComparison.Ordinal), "Data Protection key material must be encrypted."));
        var ttl = await database.KeyTimeToLiveAsync("{pole:realtime}:ticket:" + session);
        Assert.InRange(ttl!.Value, TimeSpan.Zero, TimeSpan.FromMinutes(5));
        await secondTickets.RemoveAsync(session);
        await Assert.ThrowsAsync<RealtimeSessionUnavailableException>(() => firstTickets.RenewAsync(session, original));
        Assert.Null(await secondTickets.RetrieveAsync(session));

        var shortSession = Guid.NewGuid().ToString("N");
        await firstTickets.StoreAsync(Ticket(shortSession, clock.GetUtcNow().AddSeconds(20)));
        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.Null(await secondTickets.RetrieveAsync(shortSession));
        var corruptSession = Guid.NewGuid().ToString("N");
        await firstTickets.StoreAsync(Ticket(corruptSession, clock.GetUtcNow().AddMinutes(1)));
        var protectedBytes = (byte[])(await database.StringGetAsync("{pole:realtime}:ticket:" + corruptSession))!;
        protectedBytes[^1] ^= 1;
        await database.StringSetAsync("{pole:realtime}:ticket:" + corruptSession, protectedBytes);
        Assert.Null(await secondTickets.RetrieveAsync(corruptSession));
    }

    [Fact]
    public async Task RegistryAtomicallyMaintainsTenantIndicesReplacementAndSessionRevocation()
    {
        await using var first = fixture.Services();
        await using var second = fixture.Services();
        var one = first.GetRequiredService<IRealtimeSubscriptions>();
        var two = second.GetRequiredService<IRealtimeSubscriptions>();
        var topic = Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid().ToString("N");
        var connection = Guid.NewGuid().ToString("N");
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        var alpha = Subscription(connection, session, topic, "tenant-a", expiry);
        var beta = Subscription(connection, session, topic, "tenant-b", expiry);
        Assert.False(await two.IsSessionRevokedAsync(session, CancellationToken.None));
        await one.ReplaceAsync(connection, [alpha, beta], CancellationToken.None);
        Assert.Equal(alpha, Assert.Single(await two.FindAsync(topic, "tenant-a", CancellationToken.None)));
        Assert.Equal(beta, Assert.Single(await two.FindAsync(topic, "tenant-b", CancellationToken.None)));
        Assert.Empty(await two.FindAsync(topic, null, CancellationToken.None));
        await two.RemoveAsync(connection, [alpha.Id], CancellationToken.None);
        Assert.Empty(await one.FindAsync(topic, "tenant-a", CancellationToken.None));
        Assert.Single(await one.FindAsync(topic, "tenant-b", CancellationToken.None));

        var replacement = Subscription(connection, session, topic, "tenant-c", expiry);
        await Task.WhenAll(one.ReplaceAsync(connection, [alpha], CancellationToken.None),
            two.ReplaceAsync(connection, [replacement], CancellationToken.None));
        var remaining = (await one.FindAsync(topic, "tenant-a", CancellationToken.None))
            .Concat(await one.FindAsync(topic, "tenant-b", CancellationToken.None))
            .Concat(await one.FindAsync(topic, "tenant-c", CancellationToken.None)).ToArray();
        Assert.Single(remaining);
        var otherConnection = Guid.NewGuid().ToString("N");
        await one.ReplaceAsync(otherConnection, [Subscription(otherConnection, session, topic, "tenant-b", expiry)], CancellationToken.None);
        Assert.Equal(new[] { connection, otherConnection }.Order(), (await two.RevokeSessionAsync(session, CancellationToken.None)).Order());
        Assert.True(await one.IsSessionRevokedAsync(session, CancellationToken.None));
        Assert.Empty(await one.FindAsync(topic, "tenant-a", CancellationToken.None));
        Assert.Empty(await one.FindAsync(topic, "tenant-b", CancellationToken.None));
        Assert.Empty(await one.FindAsync(topic, "tenant-c", CancellationToken.None));
        await Assert.ThrowsAsync<RealtimeSessionUnavailableException>(() => one.ReplaceAsync(connection, [alpha], CancellationToken.None));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var racingSession = Guid.NewGuid().ToString("N");
            var racingConnection = Guid.NewGuid().ToString("N");
            var racingSubscription = Subscription(racingConnection, racingSession, topic, "tenant-racing", expiry);
            var replace = Record.ExceptionAsync(() => one.ReplaceAsync(racingConnection, [racingSubscription], CancellationToken.None));
            await Task.WhenAll(replace, two.RevokeSessionAsync(racingSession, CancellationToken.None));
            Assert.True(await replace is null or RealtimeSessionUnavailableException);
            Assert.Empty(await one.FindAsync(topic, "tenant-racing", CancellationToken.None));
        }

        var expiringConnection = Guid.NewGuid().ToString("N");
        var expiringSession = Guid.NewGuid().ToString("N");
        await one.ReplaceAsync(expiringConnection,
            [Subscription(expiringConnection, expiringSession, topic, "tenant-expiring", DateTimeOffset.UtcNow.AddMilliseconds(350))], CancellationToken.None);
        var database = first.GetRequiredService<RealtimeRedisConnection>().Database;
        Assert.NotNull(await database.KeyTimeToLiveAsync("{pole:realtime}:connection:" + expiringConnection));
        Assert.NotNull(await database.KeyTimeToLiveAsync("{pole:realtime}:session:" + expiringSession));
        await Task.Delay(500);
        Assert.Empty(await two.FindAsync(topic, "tenant-expiring", CancellationToken.None));
        Assert.False(await database.KeyExistsAsync("{pole:realtime}:connection:" + expiringConnection));
        Assert.False(await database.KeyExistsAsync("{pole:realtime}:session:" + expiringSession));
    }

    [Fact]
    public async Task RealServersShareCookieAndBackplaneAcrossRestartAndRedisOutage()
    {
        var topic = "replicas-" + Guid.NewGuid().ToString("N");
        await using var first = await StartServerAsync(topic);
        await using var second = await StartServerAsync(topic);
        var cookies = new CookieContainer();
        using var browser = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
        browser.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
        browser.DefaultRequestHeaders.Authorization = new("Bearer", "realtime-test-credential");
        var bootstrap = await browser.PostAsync(Address(first) + RealtimeFeature.Route + "/session", null);
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        browser.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync(Address(second) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1", null)).StatusCode);
        await using (var client = Client(Address(first), cookies))
        {
            var changed = new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.On<RealtimeChange>("Changed", change => changed.TrySetResult(change));
            await client.StartAsync();
            Assert.True(Assert.Single(await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
                [new[] { new RealtimeSubscriptionRequest(topic) }])).Covered);
            using var scope = second.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PublishRealtimeChange>()(new(topic, Revision: 1));
            Assert.Equal(1, (await changed.Task.WaitAsync(TimeSpan.FromSeconds(15))).Revision);
        }
        await first.DisposeAsync();
        await using var restarted = await StartServerAsync(topic);
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync(Address(restarted) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1", null)).StatusCode);
        await using var reconnected = Client(Address(restarted), cookies);
        var afterRestart = new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reconnected.On<RealtimeChange>("Changed", change => afterRestart.TrySetResult(change));
        reconnected.On("SessionRevoked", () => revoked.TrySetResult());
        await reconnected.StartAsync();
        await reconnected.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe", [new[] { new RealtimeSubscriptionRequest(topic) }]);
        using (var scope = second.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PublishRealtimeChange>()(new(topic, Revision: 2));
        Assert.Equal(2, (await afterRestart.Task.WaitAsync(TimeSpan.FromSeconds(15))).Revision);
        var registry = second.Services.GetRequiredService<IRealtimeSubscriptions>();
        var active = Assert.Single(await registry.FindAsync(topic, null, CancellationToken.None));
        var tickets = second.Services.GetRequiredService<IRealtimeTicketStore>();
        await fixture.Container.StopAsync();
        try
        {
            var failure = await Record.ExceptionAsync(async () => await tickets.RetrieveAsync(active.SessionId));
            Assert.True(failure is RedisConnectionException or RedisTimeoutException, failure?.ToString());
            Assert.Equal(HttpStatusCode.InternalServerError,
                (await browser.PostAsync(Address(second) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1", null)).StatusCode);
        }
        finally { await fixture.Container.StartAsync(); }
        await EventuallyAsync(async () => Assert.NotNull(await tickets.RetrieveAsync(active.SessionId)));
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync(Address(second) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1", null)).StatusCode);
        var afterOutage = new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        reconnected.On<RealtimeChange>("Changed", change => { if (change.Revision == 3) afterOutage.TrySetResult(change); });
        await EventuallyAsync(async () =>
        {
            using var scope = second.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PublishRealtimeChange>()(new(topic, Revision: 3));
            await afterOutage.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        });
        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync(Address(second) + RealtimeFeature.Route + "/session")).StatusCode);
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty(await registry.FindAsync(topic, null, CancellationToken.None));
        Assert.Null(await tickets.RetrieveAsync(active.SessionId));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await browser.PostAsync(Address(restarted) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    private async Task<WebApplication> StartServerAsync(string topic, string? disabledTopic = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddConfiguration(fixture.Configuration(topic));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestBearer>(JwtBearerDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRealtimeFeature(builder.Configuration, development: true);
        builder.Services.AddRealtimeRedis(builder.Configuration, development: true);
        builder.Services.AddRealtimeTopic(topic, RealtimeScope.Platform);
        if (disabledTopic is not null) builder.Services.AddRealtimeTopic(disabledTopic, RealtimeScope.Platform);
        builder.Services.AddScoped<LoadTenantMember>(_ => (tenant, user, _, _) => Task.FromResult<LoadTenantMemberOutcome>(new TenantMemberMissing(tenant, user)));
        builder.Services.AddScoped<LoadTenantIdentity>(_ => (tenant, _) => Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentityNotFound(tenant)));
        builder.Services.AddScoped<IMediator, Pole.Mediator.Mediator>();
        builder.Services.AddScoped<IMediatorOutcomeHandler<CreateRealtimeSessionRequest, CreateRealtimeSessionOutcome>, CreateRealtimeSessionHandler>();
        builder.Services.AddScoped<IMediatorOutcomeHandler<DeleteRealtimeSessionRequest, DeleteRealtimeSessionOutcome>, DeleteRealtimeSessionHandler>();
        var app = builder.Build();
        app.UseRealtimeOriginGuard();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRealtimeEndpoints();
        await app.StartAsync();
        return app;
    }

    private static HubConnection Client(string address, CookieContainer cookies) => new HubConnectionBuilder()
        .WithUrl(address + RealtimeFeature.Route + "/hub", options =>
        {
            options.Cookies = cookies;
            options.Headers["Origin"] = "https://frontend.test";
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
        }).Build();

    private static string Address(WebApplication app) => app.Urls.Single();
    private static AuthenticationTicket Ticket(string session, DateTimeOffset expiry) => new(
        new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "viewer"), new Claim("iss", "https://issuer.test"),
            new Claim("sid", "idp-session"), new Claim("roles", "pole-platform-admin"),
            new Claim(RealtimeFeature.SessionClaim, session), new Claim("credential", "raw-bearer-secret")
        }, RealtimeFeature.CookieScheme)), new AuthenticationProperties { ExpiresUtc = expiry }, RealtimeFeature.CookieScheme);

    private static RealtimeSubscription Subscription(string connection, string session, string topic, string tenant, DateTimeOffset expiry) =>
        new(Guid.NewGuid().ToString("N"), connection, session, new(topic, tenant), expiry);

    private static async Task EventuallyAsync(Func<Task> assertion)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (true)
        {
            try { await assertion(); return; }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(200); }
        }
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class TestBearer(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization != "Bearer realtime-test-credential") return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("sub", "replica-user"), new Claim("iss", "https://issuer.test"),
                new Claim("roles", "pole-platform-admin"),
                new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString())
            }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
