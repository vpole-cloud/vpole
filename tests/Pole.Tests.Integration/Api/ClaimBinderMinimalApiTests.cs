using System.Net;
using Pole.Tests.Integration.Http;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class ClaimBinderMinimalApiTests
{
    [Fact]
    public async Task FromClaimAttribute_OnMinimalApiParameter_DoesNotUseMvcModelBinder()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim("sub")] string userId) => Results.Ok(userId));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FromClaimAttribute_OnMinimalApiAsParametersProperty_DoesNotUseMvcModelBinder()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([AsParameters] MinimalClaimRequest request) => Results.Ok(request.UserId));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisteringFromClaimBinderProvider_DoesNotChangeMinimalApiBinding()
    {
        await using var app = await StartApp(
            app =>
            {
                app.MapGet("/claim", ([FromClaim("sub")] string userId) => Results.Ok(userId));
            },
            services =>
            {
                services.Configure<MvcOptions>(options =>
                    options.ModelBinderProviders.Insert(0, new FromClaimBinderProvider()));
            });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ClaimValue_OnMinimalApiParameter_BindsClaimValue()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim(ClaimTypes.Role)] ClaimValue role) => Results.Ok(role.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("platform-admin", await response.Content.ReadFromJsonAsync<string>());
    }

    [Fact]
    public async Task ClaimValue_OnMinimalApiParameter_ReturnsUnauthorized_WhenClaimIsMissing()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim("missing")] ClaimValue missingClaim) => Results.Ok(missingClaim.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ClaimValue_OnAsParametersRequiredInitProperty_BindsClaimValue()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([AsParameters] ClaimValuePropertyRequest request) => Results.Ok(request.Role.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("platform-admin", await response.Content.ReadFromJsonAsync<string>());
    }

    [Fact]
    public async Task GenericClaimValue_String_BindsClaimValue()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim("sub")] ClaimValue<string> userId) => Results.Ok(userId.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("user-1", await response.Content.ReadFromJsonAsync<string>());
    }

    [Fact]
    public async Task GenericClaimValue_Int_BindsClaimValue()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim("tenant_count")] ClaimValue<int> tenantCount) => Results.Ok(tenantCount.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, await response.Content.ReadFromJsonAsync<int>());
    }

    [Fact]
    public async Task GenericClaimValue_Guid_BindsClaimValue()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim("session_id")] ClaimValue<Guid> sessionId) => Results.Ok(sessionId.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Guid.Parse("98d8387c-a23e-4cba-9c2d-9ece8ab74d8a"), await response.Content.ReadFromJsonAsync<Guid>());
    }

    [Fact]
    public async Task GenericClaimValue_ListString_BindsAllClaimValues()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim(ClaimTypes.Role)] ClaimValue<List<string>> roles) => Results.Ok(roles.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["platform-admin", "tenant-owner"], await response.Content.ReadFromJsonAsync<List<string>>());
    }

    [Fact]
    public async Task GenericClaimValue_ListString_ReturnsUnauthorized_WhenClaimIsMissing()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim("missing")] ClaimValue<List<string>> missingValues) => Results.Ok(missingValues.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GenericClaimValue_OnAsParametersRequiredInitProperty_BindsClaimValue()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([AsParameters] GenericClaimValuePropertyRequest request) => Results.Ok(request.Roles.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["platform-admin", "tenant-owner"], await response.Content.ReadFromJsonAsync<List<string>>());
    }

    [Fact]
    public async Task GenericClaimValue_NullableString_ReturnsNull_WhenClaimIsMissing()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim("missing")] ClaimValue<string?> missingValue) => Results.Ok(missingValue.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GenericClaimValue_NullableInt_ReturnsNull_WhenClaimIsMissing()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim("missing")] ClaimValue<int?> missingValue) => Results.Ok(missingValue.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GenericClaimValue_NullableListString_ReturnsNull_WhenClaimIsMissing()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([FromClaim("missing")] ClaimValue<List<string>?> missingValues) => Results.Ok(missingValues.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GenericClaimValue_NullableRequiredInitProperty_ReturnsNull_WhenClaimIsMissing()
    {
        await using var app = await StartApp(app =>
        {
            app.MapGet("/claim", ([AsParameters] OptionalGenericClaimValuePropertyRequest request) => Results.Ok(request.OptionalRole.Value));
        });

        var response = await app.GetTestClient().GetAsync("/claim");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> StartApp(
        Action<WebApplication> mapEndpoints,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (BadHttpRequestException ex)
            {
                context.Response.StatusCode = ex.StatusCode;
            }
        });
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "user-1"),
                new Claim(ClaimTypes.Role, "platform-admin"),
                new Claim(ClaimTypes.Role, "tenant-owner"),
                new Claim("tenant_count", "3"),
                new Claim("session_id", "98d8387c-a23e-4cba-9c2d-9ece8ab74d8a")
            ], "test"));
            return next(context);
        });
        mapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private sealed record MinimalClaimRequest(
        [FromClaim("sub")] string UserId);

    private sealed record ClaimValuePropertyRequest
    {
        [FromClaim(ClaimTypes.Role)]
        public required ClaimValue Role { get; init; }
    }

    private sealed record GenericClaimValuePropertyRequest
    {
        [FromClaim(ClaimTypes.Role)]
        public required ClaimValue<List<string>> Roles { get; init; }
    }

    private sealed record OptionalGenericClaimValuePropertyRequest
    {
        [FromClaim("missing")]
        public required ClaimValue<string?> OptionalRole { get; init; }
    }
}
