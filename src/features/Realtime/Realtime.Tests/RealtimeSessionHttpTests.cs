using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.Features.Realtime.Application;
using Pole.Features.Realtime.Application.Endpoints;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Mediator;
using Xunit;

namespace Pole.Features.Realtime.Tests;

public sealed class RealtimeSessionHttpTests
{
    [Fact]
    public async Task BootstrapRequiresBearerAndCookieCannotAuthenticateOrdinaryApi()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync(RealtimeFeature.Route + "/session", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "viewer");
        var response = await client.PostAsync(RealtimeFeature.Route + "/session", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RealtimeSessionCreated>();
        Assert.InRange(result!.ExpiresAtUtc - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=" + RealtimeFeature.Route, cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("viewer", cookie);
        var tickets = app.Services.GetRequiredService<MemoryTickets>();
        var ticket = Assert.Single(tickets.Tickets).Value;
        Assert.All(
            ticket.Principal.Claims,
            claim => Assert.Contains(claim.Type, new[] { "sub", "iss", "sid", "roles", RealtimeFeature.SessionClaim }));
        Assert.Empty(ticket.Properties.GetTokens());
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(RealtimeFeature.Route + "/unknown")).StatusCode);
        client.DefaultRequestHeaders.Add("Upgrade", "websocket");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(RealtimeFeature.Route + "/unknown")).StatusCode);
        client.DefaultRequestHeaders.Remove("Upgrade");
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(RealtimeFeature.Route + "/unknown")).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/ordinary-api")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync(RealtimeFeature.Route + "/session", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync(RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync(RealtimeFeature.Route + "/session")).StatusCode);
        Assert.Empty(tickets.Tickets);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync(RealtimeFeature.Route + "/hub/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Fact]
    public async Task OriginsAndAccountSwitchAreExplicitlyRejected()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "viewer");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync(RealtimeFeature.Route + "/session", null)).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.test");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsync(RealtimeFeature.Route + "/session", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
        var response = await client.PostAsync(RealtimeFeature.Route + "/session", null);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "another-account");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PostAsync(RealtimeFeature.Route + "/session", null)).StatusCode);
        Assert.Single(app.Services.GetRequiredService<MemoryTickets>().Tickets);
    }

    private static async Task<WebApplication> StartAsync(
        ObservedSessionHandler? handler = null,
        CancellationToken? requestAborted = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Realtime:Enabled"] = "true",
                ["Realtime:AllowedOrigins:0"] = "https://frontend.test",
            });
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, TestBearer>(JwtBearerDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRealtimeFeature(builder.Configuration, development: true);
        builder.Services.AddSingleton<MemoryTickets>();
        builder.Services.AddSingleton<IRealtimeTicketStore>(provider => provider.GetRequiredService<MemoryTickets>());
        builder.Services.AddSingleton<IRealtimeSubscriptions, MemorySubscriptions>();
        builder.Services.AddScoped<LoadTenantMember>(_ => (
            request, _,
            _) => Task.FromResult<LoadTenantMemberOutcome>(new TenantMemberMissing(request.TenantId, request.UserId)));
        builder.Services.AddScoped<LoadTenantIdentity>(_ => (tenant, _) =>
            Task.FromResult<LoadTenantIdentityOutcome>(new TenantIdentityNotFound(tenant)));
        builder.Services.AddScoped<IMediator, Mediator.Mediator>();
        builder.Services
            .AddScoped<IMediatorOutcomeHandler<CreateRealtimeSessionRequest, CreateRealtimeSessionOutcome>,
                CreateRealtimeSessionHandler>();
        builder.Services
            .AddScoped<IMediatorOutcomeHandler<DeleteRealtimeSessionRequest, DeleteRealtimeSessionOutcome>,
                DeleteRealtimeSessionHandler>();
        if (handler is not null)
            builder.Services
                .AddSingleton<IMediatorOutcomeHandler<CreateRealtimeSessionRequest, CreateRealtimeSessionOutcome>>(
                    handler);
        var app = builder.Build();
        if (requestAborted is { } token)
            app.Use(async (context, next) =>
            {
                context.RequestAborted = token;
                await next(context);
            });
        app.UseRealtimeOriginGuard();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRealtimeEndpoints();
        app.MapGet("/ordinary-api", () => "ok").RequireAuthorization();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task BootstrapEndpointPropagatesRequestCancellationToTheSessionHandler()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new ObservedSessionHandler();
        await using var app = await StartAsync(handler, cancellation.Token);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "viewer");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync(RealtimeFeature.Route + "/session", null)).StatusCode);
        Assert.True(handler.Received.CanBeCanceled);
        Assert.Equal(cancellation.Token, handler.Received);
    }

    [Fact]
    public async Task CookieNeverOutlivesBearerExpiry()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "viewer");
        var expiry = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        client.DefaultRequestHeaders.Add("X-Test-Expiry", expiry.ToString());
        var response = await client.PostAsync(RealtimeFeature.Route + "/session", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RealtimeSessionCreated>();
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expiry), body!.ExpiresAtUtc);
    }

    [Fact]
    public async Task DisabledCapabilityStartsWithoutRedisOrAuthenticationStores()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Realtime:AllowedOrigins:0"] = "https://frontend.test",
            });
        builder.Services.AddRealtimeFeature(builder.Configuration, development: true);
        await using var app = builder.Build();
        app.UseRealtimeOriginGuard();
        app.UseCors();
        app.MapRealtimeEndpoints();
        await app.StartAsync();
        await app.Services.GetRequiredService<PublishRealtimeChange>()(new RealtimeChange("not-enabled"));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("Origin", "https://frontend.test");
        var response = await client.PostAsync(RealtimeFeature.Route + "/session", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("https://frontend.test", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    private sealed class ObservedSessionHandler
        : IMediatorOutcomeHandler<CreateRealtimeSessionRequest, CreateRealtimeSessionOutcome>
    {
        public CancellationToken Received { get; private set; }

        public Task<CreateRealtimeSessionOutcome> HandleAsync(
            CreateRealtimeSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            Received = cancellationToken;
            return Task.FromResult<CreateRealtimeSessionOutcome>(new RealtimeSessionIdentityUnavailable());
        }
    }

    private sealed class TestBearer(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[]
                    {
                        new Claim("sub", header[7..]), new Claim("iss", "https://issuer.test"),
                        new Claim(
                            "exp",
                            Request.Headers["X-Test-Expiry"].FirstOrDefault() ??
                            DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString()),
                        new Claim("name", "must-not-be-stored"), new Claim("credential", "must-not-be-stored"),
                    },
                    Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
