using System.Net;
using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Pole.Server.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.Search.Application.Common;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Middleware;
using Pole.Features.Tenants.Application.Persistence.Tenants;
using Pole.Features.Tenants.Domain;
using Pole.Platform.Hosting;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

public sealed class TenantContextHttpTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";

    [Fact]
    public async Task ProductionEndpointGraphDeclaresEveryTenantRequest()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddTenantsFeature();
        builder.Services.AddPoleMediator();
        await using var app = builder.Build();
        app.MapApiEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        var tenantEndpoints = endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<MethodInfo>()?
            .GetParameters().Any(parameter => typeof(ITenantOperation).IsAssignableFrom(parameter.ParameterType)) == true)
            .ToArray();
        Assert.NotEmpty(tenantEndpoints);
        Assert.All(tenantEndpoints, endpoint => Assert.Equal(ActiveTenantRequirement.Required,
            endpoint.Metadata.GetMetadata<TenantEndpointMetadata>()?.ActiveTenant));
        Assert.Contains(tenantEndpoints, endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetSearch");
        Assert.Contains(tenantEndpoints, endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "CreateRedisService");
        Assert.Contains(tenantEndpoints, endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "CreatePostgresCluster");
        Assert.Contains(tenantEndpoints, endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "CreateSecret");
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
    public async Task MetadataResolvesTenantBeforeBindingOnAnUnlistedRoute(string header)
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
    public async Task UnknownTenantAndNonmemberCannotReachTheEndpoint(
        string tenantHeader,
        HttpStatusCode expected)
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
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10")).StatusCode);
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TenantId);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10")).StatusCode);
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
        Assert.Equal("none", await client.GetStringAsync("/independent-target"));
    }

    [Fact]
    public async Task AnonymousCredentialExceptionIsBoundToItsDeclaredDestination()
    {
        await using var app = await StartAsync(authenticated: false);
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/credential")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "pole_ci_unvalidated");
        Assert.Equal("credential-handler", await client.GetStringAsync("/credential"));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/unlisted/redis?PageNumber=1&PageSize=10")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/unrelated-anonymous")).StatusCode);
    }

    [Fact]
    public async Task MissingTenantDeclarationFailsEndpointConstruction()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(omitDeclaration: true));
        Assert.Contains("must declare required active tenant context", exception.Message);
    }

    private static async Task<WebApplication> StartAsync(
        string userId = "member",
        bool platformAdmin = false,
        bool authenticated = true,
        bool omitDeclaration = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTenantsFeature();
        builder.Services.AddPoleMediator();
        builder.Services.AddAuthorization();
        var tenant = TenantFactory.Create("test") with
        {
            Id = TenantId,
            Members = [TenantFactory.CreateMember(TenantId, "member", "member@example.test", TenantRole.Member)]
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
        var nested = app.MapGroup("/unlisted").ValidateTenantContextDeclarations()
            .MapGroup("");
        if (!omitDeclaration)
            nested.WithMetadata(new TenantEndpointMetadata());
        nested.MapGet("/redis", (
                [AsParameters] GetRedisServicesRequest request,
                ITenantContextAccessor accessor) =>
            {
                Assert.Equal(accessor.Current?.TenantId, request.TenantId);
                return Results.Text(request.TenantId);
            });
        nested.MapGet("/search", ([AsParameters] GetSearchRequest request, ITenantContextAccessor accessor) =>
        {
            Assert.Equal(accessor.Current?.TenantId, request.TenantId);
            return Results.Text(request.TenantId);
        });
        app.MapGet("/optional", (ITenantContextAccessor accessor) => Results.Text(accessor.Current?.TenantId ?? "none"))
            .WithMetadata(new TenantEndpointMetadata(ActiveTenantRequirement.Optional));
        app.MapGet("/independent-target", (ITenantContextAccessor accessor) => Results.Text(accessor.Current?.TenantId ?? "none"))
            .WithMetadata(new TenantEndpointMetadata(ActiveTenantRequirement.None));
        app.MapGet("/credential", () => Results.Text("credential-handler"))
            .WithMetadata(new TenantEndpointMetadata(), new TenantCredentialMetadata("pole_ci_"))
            .AllowAnonymous();
        app.MapGet("/unrelated-anonymous", () => Results.Text("must-not-run"))
            .WithMetadata(new TenantEndpointMetadata())
            .AllowAnonymous();
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
