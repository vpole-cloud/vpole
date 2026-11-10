using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Endpoints.Sources.Previews;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Platform.Hosting;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class HttpEnumContractTests
{
    [Fact]
    public async Task Named_enums_roundtrip_with_declared_casing_and_nullable_values()
    {
        await using var app = await StartApp();
        using var client = app.GetTestClient();
        using var response = await client.PostAsync(
            "/enums",
            Json(
                """
                {"role":"Admin","phase":"Bind","deployment":"Applied","enforcement":"Supported"}
                """));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Admin", body.RootElement.GetProperty("role").GetString());
        Assert.Equal("Bind", body.RootElement.GetProperty("phase").GetString());
        Assert.Equal("Applied", body.RootElement.GetProperty("deployment").GetString());
        Assert.Equal("Supported", body.RootElement.GetProperty("enforcement").GetString());
        using var nullable = await client.PostAsync(
            "/enums",
            Json(
                """
                {"role":"Viewer","phase":null,"deployment":"Prepared","enforcement":"Unknown"}
                """));
        Assert.Equal(HttpStatusCode.OK, nullable.StatusCode);
        using var nullableBody = JsonDocument.Parse(await nullable.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, nullableBody.RootElement.GetProperty("phase").ValueKind);
    }

    [Theory]
    [InlineData("role", "0")]
    [InlineData("role", "999")]
    [InlineData("phase", "0")]
    [InlineData("phase", "999")]
    [InlineData("deployment", "0")]
    [InlineData("deployment", "999")]
    [InlineData("enforcement", "0")]
    [InlineData("enforcement", "999")]
    [InlineData("role", "\"0\"")]
    [InlineData("phase", "\"999\"")]
    [InlineData("role", "\"NotARole\"")]
    [InlineData("phase", "\"NotAPhase\"")]
    [InlineData("deployment", "\"NotAStatus\"")]
    [InlineData("enforcement", "\"NotAnEnforcement\"")]
    public async Task Numeric_and_invalid_enums_fail_HTTP_binding(string property, string value)
    {
        await using var app = await StartApp();
        using var client = app.GetTestClient();
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"role":"Admin","phase":"Bind","deployment":"Applied","enforcement":"Supported"}
            """)!;
        body[property] = JsonSerializer.Deserialize<JsonElement>(value);
        using var response = await client.PostAsync("/enums", Json(JsonSerializer.Serialize(body)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("\"Supported\"", HttpStatusCode.OK)]
    [InlineData("0", HttpStatusCode.BadRequest)]
    [InlineData("999", HttpStatusCode.BadRequest)]
    [InlineData("\"invalid\"", HttpStatusCode.BadRequest)]
    public async Task Endpoint_body_uses_the_HTTP_policy(string enforcement, HttpStatusCode expected)
    {
        await using var app = await StartApp();
        using var client = app.GetTestClient();
        using var response = await client.PostAsync(
            "/network-policy",
            Json($$"""{"enforcement":{{enforcement}},"allowIsolationNotEnforced":false,"revision":1}"""));
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Supported", body.RootElement.GetProperty("enforcement").GetString());
        }
    }

    [Theory]
    [InlineData("/git-source", "prune", "Retain")]
    [InlineData("/web-service", "creationMode", "Image")]
    [InlineData("/web-service", "exposure", "Private")]
    [InlineData("/resource-changes", "removal", "Remove")]
    public async Task Endpoint_and_custom_property_converters_reject_numbers(
        string path,
        string property,
        string name)
    {
        await using var app = await StartApp();
        using var client = app.GetTestClient();
        using var valid = await client.PostAsync(path, Json($$"""{"{{property}}":"{{name}}"}"""));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var named = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
        Assert.Equal(name, named.RootElement.GetProperty(property).GetString());
        foreach (var invalid in new[] { "0", "999", "\"0\"", "\"Invalid\"" })
        {
            using var rejected = await client.PostAsync(path, Json($$"""{"{{property}}":{{invalid}}}"""));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
    }

    [Fact]
    public void Endpoint_properties_cannot_override_the_policy_with_permissive_enum_converters()
    {
        var assemblies = PolePlatform.ApplicationAssemblies.Append(typeof(CreateWebServiceRequestBody).Assembly);
        var overrides = assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace?.Contains(".Endpoints", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetProperties())
            .Where(property => property.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType is { } converter &&
                (converter == typeof(JsonStringEnumConverter) || (converter.IsGenericType &&
                    converter.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>))))
            .Select(property => $"{property.DeclaringType!.FullName}.{property.Name}");
        Assert.Empty(overrides);
    }

    [Fact]
    public void HTTP_policy_registration_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddPoleHttpJson().AddPoleHttpJson();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<JsonOptions>>().Value;
        Assert.Single(options.SerializerOptions.Converters);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TenantRole>("1", options.SerializerOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((TenantRole)999, options.SerializerOptions));
    }

    [Fact]
    public void HTTP_policy_does_not_change_default_internal_JSON_options()
    {
        Assert.Equal("2", JsonSerializer.Serialize(TenantRole.Admin));
        Assert.Equal("\"Admin\"", JsonSerializer.Serialize(TenantRole.Admin, PoleHttpJson.CreateSerializerOptions()));
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static async Task<WebApplication> StartApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddPoleHttpJson();
        var app = builder.Build();
        app.MapPost("/enums", (EnumBody body) => body);
        app.MapPost("/network-policy", (UpdateDeploymentTargetNetworkPolicyBody body) => body);
        app.MapPost("/git-source", (CreateRepositorySourceBody body) => body);
        app.MapPost("/web-service", (CreateWebServiceRequestBody body) => body);
        app.MapPost("/resource-changes", (ResourceChangesPreviewBody body) => body);
        await app.StartAsync();
        return app;
    }

    public sealed record EnumBody(
        TenantRole Role,
        DeclarationPhase? Phase,
        ResourceDeploymentStatus Deployment,
        NetworkPolicyEnforcementStatus Enforcement
    );
}
