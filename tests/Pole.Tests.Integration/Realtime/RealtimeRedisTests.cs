using System.Net;
using System.Net.Sockets;
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
    private readonly string _directory = Path.GetFullPath(Path.Combine(RepositoryPaths.FindRoot(),
        ".artifacts",
        "realtime",
        "tests",
        Guid.NewGuid().ToString("N")));

    public IContainer Container { get; private set; } = null!;

    public string CertificatePath => Path.Combine(_directory, "key-protection.pfx");
    public string ConnectionString => Container.Hostname + ":" + Container.GetMappedPublicPort(6379);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Pole realtime test keys",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate =
            request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        await File.WriteAllBytesAsync(CertificatePath, certificate.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(CertificatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // Docker may assign a different ephemeral host port when a stopped container restarts.
        // Existing application multiplexers must reconnect to the same endpoint.
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var hostPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        Container = new ContainerBuilder("redis:7.4-alpine")
            .WithTestOwnership(TestRunIdentity.Create())
            .WithPortBinding(hostPort, 6379)
            .WithCommand("--appendonly", "yes", "--appendfsync", "always", "--maxmemory-policy", "noeviction")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();
        portReservation.Stop();
        await Container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (Container is not null)
            await Container.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    public IConfiguration Configuration(string? topic = null) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
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
        if (clock is not null)
            services.AddSingleton(clock);
        services.AddRealtimeFeature(configuration, development: true);
        services.AddRealtimeRedis(configuration, development: true);
        return services.BuildServiceProvider(validateScopes: true);
    }
}

public sealed class RealtimeRedisTests(RealtimeRedisFixture fixture) : IClassFixture<RealtimeRedisFixture>
{
    [Fact]
    public async Task UserDiscoveryIsolatedAcrossCookieSessionsAndNeverUsesWildcardDelivery()
    {
        var topic = "user-discovery-" + Guid.NewGuid().ToString("N");
        await using var server = await StartServerAsync(topic, scope: RealtimeScope.User);
        var firstCookies = new CookieContainer();
        var secondCookies = new CookieContainer();
        foreach (var (cookies, credential) in new[]
                     { (firstCookies, "realtime-test-credential"), (secondCookies, "realtime-other-credential") })
        {
            using var bootstrap = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
            bootstrap.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
            bootstrap.DefaultRequestHeaders.Authorization = new("Bearer", credential);
            using var response = await bootstrap.PostAsync(Address(server) + RealtimeFeature.Route + "/session", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using var first = Client(Address(server), firstCookies);
        await using var second = Client(Address(server), secondCookies);
        var firstChanges = new System.Collections.Concurrent.ConcurrentQueue<RealtimeChange>();
        var secondChanges = new System.Collections.Concurrent.ConcurrentQueue<RealtimeChange>();
        var firstRevocations = 0;
        first.On<RealtimeChange>("Changed", change => firstChanges.Enqueue(change));
        second.On<RealtimeChange>("Changed", change => secondChanges.Enqueue(change));
        first.On<string?>("AccessRevoked", _ => Interlocked.Increment(ref firstRevocations));
        await first.StartAsync();
        await second.StartAsync();
        await first.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
            [new RealtimeSubscriptionRequest[] { new(topic, ResourceId: "replica-user") }]);
        await second.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
            [new RealtimeSubscriptionRequest[] { new(topic, ResourceId: "other-user") }]);
        await Assert.ThrowsAsync<HubException>(() => first.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>(
            "Subscribe",
            [new RealtimeSubscriptionRequest[] { new(topic, ResourceId: "other-user") }]));
        Assert.Equal(0, Volatile.Read(ref firstRevocations));
        // A rejected foreign-subject replacement may remove its user interests but cannot
        // revoke platform/tenant access. A valid own-subject subscription recovers normally.
        await first.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
            [new RealtimeSubscriptionRequest[] { new(topic, ResourceId: "replica-user") }]);
        using var provider = server.Services.CreateScope();
        var publish = provider.ServiceProvider.GetRequiredService<PublishRealtimeChange>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => publish(new(topic)));
        await publish(new(topic, ResourceId: "replica-user", Revision: 1));
        await publish(new(topic, ResourceId: "other-user", Revision: 2));
        // Hub replies share each connection's ordered transport queue with notifications.
        await first.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", []);
        await second.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", []);
        Assert.Equal("replica-user", Assert.Single(firstChanges).ResourceId);
        Assert.Equal("other-user", Assert.Single(secondChanges).ResourceId);
    }

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
        client.On("SessionRevoked",
            () =>
            {
                Interlocked.Increment(ref sessionRevocations);
                sessionRevoked.TrySetResult();
            });
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
        await Assert.ThrowsAsync<HubException>(() =>
            client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", []));
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
            clock.GetUtcNow().AddMinutes(5).AddSeconds(-1),
            clock.GetUtcNow().AddMinutes(5));
        Assert.Empty(restored.Properties.GetTokens());
        Assert.DoesNotContain("private", restored.Properties.Items.Keys);
        Assert.All(restored.Principal.Claims,
            claim => Assert.Contains(claim.Type,
                new[] { "iss", "sub", "sid", "roles", RealtimeFeature.SessionClaim }));
        var database = first.GetRequiredService<RealtimeRedisConnection>().Database;
        var raw = await database.StringGetAsync("{pole:realtime}:ticket:" + session);
        Assert.DoesNotContain("raw-bearer-secret", Encoding.UTF8.GetString((byte[])raw!));
        var keyring = await database.ListRangeAsync("{pole:realtime}:data-protection");
        Assert.NotEmpty(keyring);
        Assert.All(keyring,
            item => Assert.True(item.ToString().Contains("encryptedSecret", StringComparison.Ordinal),
                "Data Protection key material must be encrypted."));
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
        Assert.Equal(new[] { alpha.Id, beta.Id }.Order(),
            (await two.FindAllAsync(topic, CancellationToken.None)).Select(subscription => subscription.Id).Order());
        Assert.Empty(await two.FindAsync(topic, null, CancellationToken.None));
        await two.RemoveAsync(connection, [alpha.Id], CancellationToken.None);
        Assert.Empty(await one.FindAsync(topic, "tenant-a", CancellationToken.None));
        Assert.Single(await one.FindAsync(topic, "tenant-b", CancellationToken.None));
        Assert.Equal(beta, Assert.Single(await one.FindAllAsync(topic, CancellationToken.None)));

        var replacement = Subscription(connection, session, topic, "tenant-c", expiry);
        await Task.WhenAll(one.ReplaceAsync(connection, [alpha], CancellationToken.None),
            two.ReplaceAsync(connection, [replacement], CancellationToken.None));
        var remaining = (await one.FindAsync(topic, "tenant-a", CancellationToken.None))
            .Concat(await one.FindAsync(topic, "tenant-b", CancellationToken.None))
            .Concat(await one.FindAsync(topic, "tenant-c", CancellationToken.None)).ToArray();
        Assert.Single(remaining);
        Assert.Equal(remaining, await two.FindAllAsync(topic, CancellationToken.None));
        var otherConnection = Guid.NewGuid().ToString("N");
        await one.ReplaceAsync(otherConnection,
            [Subscription(otherConnection, session, topic, "tenant-b", expiry)],
            CancellationToken.None);
        Assert.Equal(new[] { connection, otherConnection }.Order(),
            (await two.RevokeSessionAsync(session, CancellationToken.None)).Order());
        Assert.True(await one.IsSessionRevokedAsync(session, CancellationToken.None));
        Assert.Empty(await one.FindAsync(topic, "tenant-a", CancellationToken.None));
        Assert.Empty(await one.FindAsync(topic, "tenant-b", CancellationToken.None));
        Assert.Empty(await one.FindAsync(topic, "tenant-c", CancellationToken.None));
        Assert.Empty(await one.FindAllAsync(topic, CancellationToken.None));
        await Assert.ThrowsAsync<RealtimeSessionUnavailableException>(() =>
            one.ReplaceAsync(connection, [alpha], CancellationToken.None));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var racingSession = Guid.NewGuid().ToString("N");
            var racingConnection = Guid.NewGuid().ToString("N");
            var racingSubscription = Subscription(racingConnection, racingSession, topic, "tenant-racing", expiry);
            var replace = Record.ExceptionAsync(() =>
                one.ReplaceAsync(racingConnection, [racingSubscription], CancellationToken.None));
            await Task.WhenAll(replace, two.RevokeSessionAsync(racingSession, CancellationToken.None));
            Assert.True(await replace is null or RealtimeSessionUnavailableException);
            Assert.Empty(await one.FindAsync(topic, "tenant-racing", CancellationToken.None));
        }

        var expiringConnection = Guid.NewGuid().ToString("N");
        var expiringSession = Guid.NewGuid().ToString("N");
        await one.ReplaceAsync(expiringConnection,
            [
                Subscription(expiringConnection,
                    expiringSession,
                    topic,
                    "tenant-expiring",
                    DateTimeOffset.UtcNow.AddMilliseconds(350))
            ],
            CancellationToken.None);
        var database = first.GetRequiredService<RealtimeRedisConnection>().Database;
        Assert.NotNull(await database.KeyTimeToLiveAsync("{pole:realtime}:connection:" + expiringConnection));
        Assert.NotNull(await database.KeyTimeToLiveAsync("{pole:realtime}:session:" + expiringSession));
        await Task.Delay(500);
        Assert.Empty(await two.FindAsync(topic, "tenant-expiring", CancellationToken.None));
        Assert.Empty(await two.FindAllAsync(topic, CancellationToken.None));
        Assert.False(await database.KeyExistsAsync("{pole:realtime}:connection:" + expiringConnection));
        Assert.False(await database.KeyExistsAsync("{pole:realtime}:session:" + expiringSession));
    }

    [Fact]
    public async Task GracefulServerStopWaitsForDisconnectedSubscriptionRemoval()
    {
        var topic = "disconnect-" + Guid.NewGuid().ToString("N");
        await using var server = await StartServerAsync(topic, gateDisconnect: true);
        var gate = server.Services.GetRequiredService<GatedDisconnectSubscriptions>();
        var cookies = new CookieContainer();
        using var browser = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
        browser.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
        browser.DefaultRequestHeaders.Authorization = new("Bearer", "realtime-test-credential");
        Assert.Equal(HttpStatusCode.OK,
            (await browser.PostAsync(Address(server) + RealtimeFeature.Route + "/session", null)).StatusCode);
        var client = Client(Address(server), cookies);
        Task? disposal = null;
        try
        {
            await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
                [new[] { new RealtimeSubscriptionRequest(topic) }]).WaitAsync(TimeSpan.FromSeconds(15));
            var clientDisposal = client.DisposeAsync().AsTask();
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await clientDisposal.WaitAsync(TimeSpan.FromSeconds(15));
            disposal = StopServerAsync(server);
            await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(15));
            await client.DisposeAsync();
        }
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
        Assert.Equal(HttpStatusCode.OK,
            (await browser.PostAsync(Address(second) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1",
                null)).StatusCode);
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

        await StopServerAsync(first);
        await using var restarted = await StartServerAsync(topic);
        Assert.Equal(HttpStatusCode.OK,
            (await browser.PostAsync(Address(restarted) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1",
                null)).StatusCode);
        await using var reconnected = Client(Address(restarted), cookies);
        var afterRestart = new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reconnected.On<RealtimeChange>("Changed", change => afterRestart.TrySetResult(change));
        reconnected.On("SessionRevoked", () => revoked.TrySetResult());
        await reconnected.StartAsync();
        await reconnected.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
            [new[] { new RealtimeSubscriptionRequest(topic) }]);
        using (var scope = second.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PublishRealtimeChange>()(new(topic, Revision: 2));
        Assert.Equal(2, (await afterRestart.Task.WaitAsync(TimeSpan.FromSeconds(15))).Revision);
        var registry = second.Services.GetRequiredService<IRealtimeSubscriptions>();
        var active = Assert.Single(await registry.FindAsync(topic, null, CancellationToken.None));
        var tickets = second.Services.GetRequiredService<IRealtimeTicketStore>();
        var endpointBeforeRestart = fixture.ConnectionString;
        await fixture.Container.StopAsync();
        try
        {
            var failure = await Record.ExceptionAsync(async () => await tickets.RetrieveAsync(active.SessionId));
            Assert.True(failure is RedisConnectionException or RedisTimeoutException, failure?.ToString());
            Assert.Equal(HttpStatusCode.InternalServerError,
                (await browser.PostAsync(Address(second) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1",
                    null)).StatusCode);
        }
        finally
        {
            await fixture.Container.StartAsync();
        }

        Assert.Equal(endpointBeforeRestart, fixture.ConnectionString);
        using (var probe = await ConnectionMultiplexer.ConnectAsync(
                   endpointBeforeRestart + ",connectRetry=0,connectTimeout=5000,asyncTimeout=3000"))
            await probe.GetDatabase().PingAsync();
        await EventuallyAsync(async () => Assert.NotNull(await tickets.RetrieveAsync(active.SessionId)));
        Assert.Equal(HttpStatusCode.OK,
            (await browser.PostAsync(Address(second) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1",
                null)).StatusCode);
        var afterOutage = new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        reconnected.On<RealtimeChange>("Changed",
            change =>
            {
                if (change.Revision == 3)
                    afterOutage.TrySetResult(change);
            });
        await EventuallyAsync(async () =>
        {
            using var scope = second.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PublishRealtimeChange>()(new(topic, Revision: 3));
            await afterOutage.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        });
        Assert.Equal(HttpStatusCode.NoContent,
            (await browser.DeleteAsync(Address(second) + RealtimeFeature.Route + "/session")).StatusCode);
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty(await registry.FindAsync(topic, null, CancellationToken.None));
        Assert.Null(await tickets.RetrieveAsync(active.SessionId));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await browser.PostAsync(Address(restarted) + RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1",
                null)).StatusCode);
    }

    private async Task<WebApplication> StartServerAsync(string topic,
        string? disabledTopic = null,
        RealtimeScope scope = RealtimeScope.Platform,
        bool gateDisconnect = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddConfiguration(fixture.Configuration(topic));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestBearer>(JwtBearerDefaults.AuthenticationScheme,
                _ =>
                {
                });
        builder.Services.AddAuthorization();
        builder.Services.AddRealtimeFeature(builder.Configuration, development: true);
        builder.Services.AddRealtimeRedis(builder.Configuration, development: true);
        if (gateDisconnect)
        {
            builder.Services.AddSingleton(provider => new GatedDisconnectSubscriptions(
                new RedisRealtimeSubscriptions(provider.GetRequiredService<RealtimeRedisConnection>(),
                    provider.GetRequiredService<TimeProvider>())));
            builder.Services.AddSingleton<IRealtimeSubscriptions>(provider =>
                provider.GetRequiredService<GatedDisconnectSubscriptions>());
        }
        builder.Services.AddRealtimeTopic(topic, scope);
        if (disabledTopic is not null)
            builder.Services.AddRealtimeTopic(disabledTopic, RealtimeScope.Platform);
        builder.Services.AddScoped<LoadTenantMember>(_ => (tenant, user, _, _) =>
            Task.FromResult<LoadTenantMemberOutcome>(new TenantMemberMissing(tenant, user)));
        builder.Services.AddScoped<LoadTenantIdentity>(_ => (tenant, _) =>
            Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentityNotFound(tenant)));
        builder.Services.AddScoped<IMediator, Pole.Mediator.Mediator>();
        builder.Services
            .AddScoped<IMediatorOutcomeHandler<CreateRealtimeSessionRequest, CreateRealtimeSessionOutcome>,
                CreateRealtimeSessionHandler>();
        builder.Services
            .AddScoped<IMediatorOutcomeHandler<DeleteRealtimeSessionRequest, DeleteRealtimeSessionOutcome>,
                DeleteRealtimeSessionHandler>();
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
        .WithUrl(address + RealtimeFeature.Route + "/hub",
            options =>
            {
                options.Cookies = cookies;
                options.Headers["Origin"] = "https://frontend.test";
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            }).Build();

    private static string Address(WebApplication app) => app.Urls.Single();

    private static Task StopServerAsync(WebApplication server)
    {
        var cleanup = new TestResourceScope("Stop realtime test server");
        cleanup.Defer("Dispose realtime test server", _ => server.DisposeAsync().AsTask());
        return cleanup.RunAsync(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await server.StopAsync(deadline.Token).WaitAsync(deadline.Token);
        });
    }

    private sealed class GatedDisconnectSubscriptions(IRealtimeSubscriptions inner) : IRealtimeSubscriptions
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ReplaceAsync(string connectionId, IReadOnlyList<RealtimeSubscription> subscriptions,
            CancellationToken cancellationToken) => inner.ReplaceAsync(connectionId, subscriptions, cancellationToken);
        public async Task RemoveAsync(string connectionId, IReadOnlyCollection<string>? subscriptionIds,
            CancellationToken cancellationToken)
        {
            // Subscribe replacement uses ConnectionAborted; OnDisconnected deliberately uses None.
            if (!cancellationToken.CanBeCanceled)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            await inner.RemoveAsync(connectionId, subscriptionIds, cancellationToken);
        }
        public Task<IReadOnlyList<RealtimeSubscription>> FindAsync(string topic, string? tenantId,
            CancellationToken cancellationToken) => inner.FindAsync(topic, tenantId, cancellationToken);
        public Task<IReadOnlyList<RealtimeSubscription>> FindAllAsync(string topic, CancellationToken cancellationToken) =>
            inner.FindAllAsync(topic, cancellationToken);
        public Task<IReadOnlyList<string>> RevokeSessionAsync(string sessionId, CancellationToken cancellationToken) =>
            inner.RevokeSessionAsync(sessionId, cancellationToken);
        public Task<bool> IsSessionRevokedAsync(string sessionId, CancellationToken cancellationToken) =>
            inner.IsSessionRevokedAsync(sessionId, cancellationToken);
    }

    private static AuthenticationTicket Ticket(string session, DateTimeOffset expiry) => new(
        new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("sub", "viewer"), new Claim("iss", "https://issuer.test"),
                new Claim("sid", "idp-session"), new Claim("roles", "pole-platform-admin"),
                new Claim(RealtimeFeature.SessionClaim, session), new Claim("credential", "raw-bearer-secret")
            },
            RealtimeFeature.CookieScheme)),
        new AuthenticationProperties { ExpiresUtc = expiry },
        RealtimeFeature.CookieScheme);

    private static RealtimeSubscription Subscription(string connection,
        string session,
        string topic,
        string tenant,
        DateTimeOffset expiry) =>
        new(Guid.NewGuid().ToString("N"), connection, session, new(topic, tenant), expiry);

    private static async Task EventuallyAsync(Func<Task> assertion)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    private sealed class TestBearer(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers.Authorization.ToString() switch
            {
                "Bearer realtime-test-credential" => "replica-user",
                "Bearer realtime-other-credential" => "other-user",
                _ => null
            };
            if (subject is null)
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("sub", subject), new Claim("iss", "https://issuer.test"),
                    new Claim("roles", "pole-platform-admin"),
                    new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString())
                },
                Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
