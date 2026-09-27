using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Mediator;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class ResultExtensionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpProblemExecutesWithTheSamePayloadThroughBothAdapters(bool legacyAdapter)
    {
        var problem = new HttpProblem(
            StatusCodes.Status409Conflict,
            "tag:pole.dev,2026:problem:users.email.already-exists",
            "User email already exists",
            "users.email.already-exists",
            "Choose another email.",
            new Dictionary<string, object> { ["email"] = "ada@example.com" });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        MediatorResult<string> result = "unused";

        var response = legacyAdapter
            ? problem.ToHttpResult()
            : result.ToHttpResult(_ => problem);
        await response.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        var payload = json.RootElement;
        Assert.Equal(problem.StatusCode, payload.GetProperty("status").GetInt32());
        Assert.Equal(problem.Type, payload.GetProperty("type").GetString());
        Assert.Equal(problem.Title, payload.GetProperty("title").GetString());
        Assert.Equal(problem.Detail, payload.GetProperty("detail").GetString());
        Assert.Equal(problem.Code, payload.GetProperty("code").GetString());
        Assert.Equal("ada@example.com", payload.GetProperty("email").GetString());
    }

    [Fact]
    public async Task PluginMediatorRejectionUsesRegisteredHttpProblemMapping()
    {
        MediatorResult<string> result = new PipelineRejected(
            new PluginPolicyRejected("deployments.create"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.AddHttpProblemMappings(
        [
            HttpProblemMapping.For<PluginPolicyRejected>(rejection =>
                new HttpProblem(
                    StatusCodes.Status403Forbidden,
                    "tag:plugin.example,2026:problem:policy-denied",
                    "Plugin policy denied",
                    "plugin.policy.denied",
                    extensions: new Dictionary<string, object>
                    {
                        ["policy"] = rejection.Policy
                    }))
        ]);
        await using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider
        };
        httpContext.Response.Body = new MemoryStream();

        var httpResult = result.ToHttpResult();
        await httpResult.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        httpContext.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(httpContext.Response.Body);
        Assert.Equal(
            "tag:plugin.example,2026:problem:policy-denied",
            json.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "plugin.policy.denied",
            json.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "deployments.create",
            json.RootElement.GetProperty("policy").GetString());
    }

    [Fact]
    public async Task UnmappedMediatorRejectionFailsWhenHttpResultExecutes()
    {
        MediatorResult<string> result = new PipelineRejected(
            new PluginPolicyRejected("deployments.create"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.AddHttpProblemMappings([]);
        await using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider
        };

        var httpResult = result.ToHttpResult();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            httpResult.ExecuteAsync(httpContext));

        Assert.Contains(
            typeof(PluginPolicyRejected).FullName!,
            exception.Message,
            StringComparison.Ordinal);
    }

    private sealed record PluginPolicyRejected(string Policy)
        : IMediatorRejection;
}
