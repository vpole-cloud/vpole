using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Mediator;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class ResultExtensionsTests
{
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
