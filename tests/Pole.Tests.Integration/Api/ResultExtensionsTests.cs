using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pole.ASP.Helpers;
using Pole.Mediator;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.Api;

public sealed class ResultExtensionsTests
{
    [Fact]
    public void SemanticValidationMapsToUnprocessableEntity()
    {
        Result<string> result = Failures.SemanticValidation(
            new Dictionary<string, string[]>
            {
                ["Placement.DeploymentTargetId"] = ["A target must be selected."]
            });

        var httpResult = result.ToHttpResult();

        Assert.Equal(
            StatusCodes.Status422UnprocessableEntity,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(httpResult).StatusCode);
        var body = Assert.IsAssignableFrom<IValueHttpResult>(httpResult).Value;
        var json = JsonSerializer.Serialize(body);
        Assert.Contains("Placement.DeploymentTargetId", json, StringComparison.Ordinal);
        Assert.Contains("A target must be selected.", json, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalFailureDoesNotExposeDiagnostics()
    {
        Result<string> result = Failures.Internal(
            "Internal operation failed.",
            "secret provider diagnostic");

        var httpResult = result.ToHttpResult();

        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(httpResult).StatusCode);
        var body = Assert.IsAssignableFrom<IValueHttpResult>(httpResult).Value;
        var json = JsonSerializer.Serialize(body);
        Assert.DoesNotContain("Internal operation failed", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret provider diagnostic", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyMediatorRejectionPreservesExistingHttpMapping()
    {
        MediatorResult<string> result = new PipelineRejected(
            new LegacyResultRejected(Failures.SemanticValidation(
                new Dictionary<string, string[]>
                {
                    ["Placement.DeploymentTargetId"] = ["A target must be selected."]
                })));

        var httpResult = result.ToHttpResult();

        Assert.Equal(
            StatusCodes.Status422UnprocessableEntity,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(httpResult).StatusCode);
        var body = Assert.IsAssignableFrom<IValueHttpResult>(httpResult).Value;
        var json = JsonSerializer.Serialize(body);
        Assert.Contains("Placement.DeploymentTargetId", json, StringComparison.Ordinal);
        Assert.Contains("A target must be selected.", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmappedMediatorRejectionFailsExplicitly()
    {
        MediatorResult<string> result = new PipelineRejected(
            new PluginPolicyRejected());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            result.ToHttpResult());

        Assert.Contains(
            typeof(PluginPolicyRejected).FullName!,
            exception.Message,
            StringComparison.Ordinal);
    }

    private sealed record PluginPolicyRejected : IMediatorRejection;
}
