using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Pole.ASP.Helpers;
using Pole.Platform.Hosting;
using Pole.Server.Extensions;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Security;

[Collection(IntegrationTestCollection.Global2)]
public sealed class HangfireDashboardTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    private const string Issuer = "https://issuer.example.test/realms/pole.control-plane";
    private readonly SymmetricSecurityKey _signingKey = new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task DevelopmentRequiresAnAdminBearerOnEveryDashboardRequest()
    {
        await using var app = await StartAsync("Development");
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        const string page = "/hangfire";
        const string stylesheet = "/hangfire/css1";
        const string jobAction = "/hangfire/jobs/failed/delete";

        using var anonymous = await client.GetAsync(page);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        using var invalid = await client.GetAsync(stylesheet);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

        foreach (var tenantRole in new[] { "Owner", "Admin" })
        {
            client.DefaultRequestHeaders.Authorization = Bearer(tenantRole);
            using var deniedPage = await client.GetAsync(page);
            using var deniedCss = await client.GetAsync(stylesheet);
            using var deniedAction = await client.PostAsync(jobAction, null);
            Assert.Equal(HttpStatusCode.Forbidden, deniedPage.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, deniedCss.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, deniedAction.StatusCode);
        }

        client.DefaultRequestHeaders.Authorization = Bearer(Roles.Admin);
        using var dashboard = await client.GetAsync(page);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal("text/html", dashboard.Content.Headers.ContentType?.MediaType);
        using var css = await client.GetAsync(stylesheet);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        using var form = new FormUrlEncodedContent([]);
        using var missingAntiforgery = await client.PostAsync(jobAction, form);
        Assert.Equal(HttpStatusCode.Forbidden, missingAntiforgery.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task DashboardIsAbsentOutsideDevelopment(string environment)
    {
        await using var app = await StartAsync(environment);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = Bearer(Roles.Admin);
        using var response = await client.GetAsync("/hangfire");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<WebApplication> StartAsync(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.AddSecurity();
        var discovery = new OpenIdConnectConfiguration { Issuer = Issuer };
        discovery.SigningKeys.Add(_signingKey);
        builder.Services.Configure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            options =>
            {
                options.Authority = Issuer;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
            });
        builder.Services.AddHangfire(config => config.UseMongoStorage(
            Database.Client,
            Database.DatabaseNamespace.DatabaseName,
            new MongoStorageOptions
            {
                MigrationOptions
                    = new MongoMigrationOptions { MigrationStrategy = new MigrateMongoMigrationStrategy() },
            }));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDevelopmentHangfireDashboard();
        await app.StartAsync();
        return app;
    }

    private AuthenticationHeaderValue Bearer(string role)
    {
        var token = new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = "pole.control-plane.api",
                Subject = new ClaimsIdentity([new Claim(Claims.Roles, role)]),
                SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
            });
        return new AuthenticationHeaderValue("Bearer", token);
    }
}
