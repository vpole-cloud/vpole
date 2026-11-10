using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Realtime.Application;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.Search.Application.Common;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Middleware;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Domain;
using Pole.Platform.Hosting;
using Pole.Server.Extensions;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

public sealed class TenantContextHttpTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";

    [Fact]
    public async Task ProductionEndpointGraphRequiresTenantContextByDefault()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRealtimeFeature(builder.Configuration, development: false);
        builder.Services.AddTenantsFeature();
        builder.Services.AddPoleMediator();
        await using var app = builder.Build();
        app.MapApiEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        var tenantEndpoints = endpoints.Where(endpoint =>
                endpoint.Metadata
                    .GetMetadata<MethodInfo>()
                    ?.GetParameters()
                    .Any(parameter => typeof(ITenantOperation).IsAssignableFrom(parameter.ParameterType)) == true)
            .ToArray();
        Assert.NotEmpty(tenantEndpoints);
        Assert.All(
            tenantEndpoints,
            endpoint => Assert.Equal(
                ActiveTenantRequirement.Required,
                endpoint.Metadata.GetMetadata<TenantEndpointMetadata>()?.ActiveTenant));
        Assert.Contains(
            tenantEndpoints,
            endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetSearch");
        Assert.Contains(
            tenantEndpoints,
            endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "CreateRedisService");
        Assert.Contains(
            tenantEndpoints,
            endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ==
                "CreatePostgresCluster");
        Assert.Contains(
            tenantEndpoints,
            endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "CreateSecret");
        var apiEndpoints = endpoints.OfType<RouteEndpoint>().ToArray();
        Assert.All(
            apiEndpoints,
            endpoint =>
            {
                var route = endpoint.RoutePattern.RawText!;
                var independent = route.StartsWith("/api/v1/platform/", StringComparison.Ordinal) ||
                    route.StartsWith("/api/v1/realtime/", StringComparison.Ordinal) ||
                    route.StartsWith("/api/v1/tenants", StringComparison.Ordinal) ||
                    route.StartsWith("/api/v1/users", StringComparison.Ordinal);
                Assert.Equal(
                    independent ? ActiveTenantRequirement.None : ActiveTenantRequirement.Required,
                    endpoint.Metadata.GetMetadata<TenantEndpointMetadata>()?.ActiveTenant);
            });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-object-id")]
    [InlineData("507f1f77bcf86cd799439011,507f1f77bcf86cd799439012")]
    public async Task RequiredContextRejectsInvalidHeaderBeforeBinding(string? tenantHeader)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        if (tenantHeader is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation(TenantHeaders.TenantId, tenantHeader);

        var response = await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("request.validation.failed", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(TenantId)]
    [InlineData("507F1F77BCF86CD799439011")]
    public async Task InheritedDefaultResolvesTenantBeforeBindingOnAnUnlistedRoute(string header)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, header);

        var response = await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TenantId, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("507f1f77bcf86cd799439012", HttpStatusCode.NotFound)]
    [InlineData(TenantId, HttpStatusCode.Forbidden)]
    public async Task UnknownTenantAndNonmemberCannotReachTheEndpoint(string tenantHeader, HttpStatusCode expected)
    {
        await using var app = await StartAsync(userId: "outsider");
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, tenantHeader);
        var response = await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdminStillNeedsSelectionButMaySelectAnUnjoinedTenant()
    {
        await using var app = await StartAsync(userId: "outsider", platformAdmin: true);
        using var client = app.GetTestClient();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10")).StatusCode);
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TenantId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedPrincipalWithoutUserIdIsUnauthorized(bool platformAdmin)
    {
        await using var app = await StartAsync(userId: "", platformAdmin: platformAdmin);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TenantId);

        var response = await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("tenant.authorization.user-identity-required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FSharpRequestBindsTheSameCanonicalSelection()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TenantId.ToUpperInvariant());
        var response = await client.GetAsync("/unlisted/search?Query=test&PageNumber=1&PageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TenantId, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MultipleActualHeaderValuesAreRejected()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, [TenantId, TenantId]);
        var response = await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OptionalContextResolvesOnlyWhenPresentAndNoneIgnoresTheHeader()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal("none", await client.GetStringAsync("/optional"));
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TenantId);
        Assert.Equal(TenantId, await client.GetStringAsync("/optional"));
        client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, "malformed");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/optional")).StatusCode);
        Assert.Equal("none", await client.GetStringAsync("/independent"));
    }

    [Fact]
    public async Task AnonymousCredentialExceptionIsBoundToItsDeclaredDestination()
    {
        await using var app = await StartAsync(authenticated: false);
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/credential")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "pole_ci_unvalidated");
        Assert.Equal("credential-handler", await client.GetStringAsync("/credential"));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/unrelated-anonymous")).StatusCode);
    }

    [Fact]
    public async Task MissingTenantDeclarationFailsEndpointConstruction()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(omitDeclaration: true));
        Assert.Contains("must declare required active tenant context", exception.Message);
    }

    [Fact]
    public async Task DisablingContextForATenantRequestFailsEndpointConstruction()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(disableContext: true));
        Assert.Contains("must declare required active tenant context", exception.Message);
    }

    [Fact]
    public async Task NewEndpointInApiGroupInheritsRequirementWithoutAMarker()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/unlisted/plain")).StatusCode);
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TenantId);
        Assert.Equal(TenantId, await client.GetStringAsync("/unlisted/plain"));
    }

    [Fact]
    public async Task TechnicalEndpointOutsideApiGroupDoesNotRequireAuthenticationOrTenant()
    {
        await using var app = await StartAsync(authenticated: false);
        using var client = app.GetTestClient();
        Assert.Equal("healthy", await client.GetStringAsync("/health"));
    }

    private static async Task<WebApplication> StartAsync(
        string userId = "member",
        bool platformAdmin = false,
        bool authenticated = true,
        bool omitDeclaration = false,
        bool disableContext = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddPoleHttpJson();
        builder.Services.AddTenantsFeature();
        builder.Services.AddPoleMediator();
        builder.Services.AddAuthorization();
        var tenant = TenantFactory.Create("test") with
        {
            Id = TenantId,
            Members = [TenantFactory.CreateMember(TenantId, "member", "member@example.test", TenantRole.Member)],
        };
        builder.Services.AddScoped<LoadTenant>(_ => (id, _) => Task.FromResult(id == TenantId ? tenant : null));
        var app = builder.Build();
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            List<Claim> claims = [new(Claims.UserId, userId)];
            if (platformAdmin)
                claims.Add(new Claim(Claims.Roles, Roles.Admin));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "test" : null));
            await next();
        });
        app.UseMiddleware<TenantContextMiddleware>();
        var api = app.MapGroup("");
        if (!omitDeclaration)
            api.RequireTenantContext();
        else
            api.ValidateTenantContextDeclarations();
        var nested = api.MapGroup("/unlisted").MapGroup("");
        if (disableContext)
            nested.WithoutTenantContext();
        nested.MapGet("/plain", (ITenantContextAccessor accessor) => Results.Text(accessor.Current!.TenantId));
        app.MapGet("/health", () => Results.Text("healthy"));
        nested.MapGet(
            "/redis",
            ([AsParameters] GetRedisServicesRequest request, ITenantContextAccessor accessor) =>
            {
                Assert.Equal(accessor.Current?.TenantId, request.TenantId);
                return Results.Text(request.TenantId);
            });
        nested.MapGet(
            "/search",
            ([AsParameters] GetSearchRequest request, ITenantContextAccessor accessor) =>
            {
                Assert.Equal(accessor.Current?.TenantId, request.TenantId);
                return Results.Text(request.TenantId);
            });
        api.MapGet("/optional", (ITenantContextAccessor accessor) => Results.Text(accessor.Current?.TenantId ?? "none"))
            .WithMetadata(new TenantEndpointMetadata(ActiveTenantRequirement.Optional));
        api.MapGet(
                "/independent",
                (ITenantContextAccessor accessor) => Results.Text(accessor.Current?.TenantId ?? "none"))
            .WithoutTenantContext();
        api.MapGet("/credential", () => Results.Text("credential-handler"))
            .WithMetadata(new TenantCredentialMetadata("pole_ci_"))
            .AllowAnonymous();
        api.MapGet("/unrelated-anonymous", () => Results.Text("must-not-run")).AllowAnonymous();
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return app;
    }
}
