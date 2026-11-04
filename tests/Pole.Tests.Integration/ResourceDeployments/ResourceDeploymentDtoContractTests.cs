using System.Text.Json;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentDtoContractTests
{
    [Theory]
    [InlineData(ResourceDeploymentStatus.Prepared, "Prepared")]
    [InlineData(ResourceDeploymentStatus.WaitingForRuntimeCell, "WaitingForRuntimeCell")]
    [InlineData(ResourceDeploymentStatus.Pending, "Pending")]
    [InlineData(ResourceDeploymentStatus.Deleting, "Deleting")]
    [InlineData(ResourceDeploymentStatus.Reconciling, "Reconciling")]
    [InlineData(ResourceDeploymentStatus.Applied, "Applied")]
    [InlineData(ResourceDeploymentStatus.DeleteFailed, "DeleteFailed")]
    [InlineData(ResourceDeploymentStatus.Failed, "Failed")]
    [InlineData(ResourceDeploymentStatus.Deleted, "Deleted")]
    public void Status_wire_values_remain_strings(ResourceDeploymentStatus status, string expected)
    {
        var json = JsonSerializer.Serialize(status);
        Assert.Equal('"' + expected + '"', json);
        Assert.Equal(status, JsonSerializer.Deserialize<ResourceDeploymentStatus>(json));
    }

    [Theory]
    [InlineData(ResourceDeploymentDesiredOperation.Apply, "Apply")]
    [InlineData(ResourceDeploymentDesiredOperation.Delete, "Delete")]
    public void Operation_wire_values_remain_strings(ResourceDeploymentDesiredOperation operation, string expected)
    {
        var json = JsonSerializer.Serialize(operation);
        Assert.Equal('"' + expected + '"', json);
        Assert.Equal(operation, JsonSerializer.Deserialize<ResourceDeploymentDesiredOperation>(json));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"unknown\"")]
    public void Unknown_or_numeric_wire_values_are_rejected(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ResourceDeploymentStatus>(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ResourceDeploymentDesiredOperation>(json));
    }

    [Fact]
    public void Placement_round_trip_preserves_typed_defaults_and_isolation()
    {
        var placement = new PlacementSummaryDto("deployment", "scope", "cell", "target", 1, 0,
            ResourceDeploymentStatus.Prepared, null, new(new("profile", 3), "Small", new(100, 128), new(200, 256), []),
            [], ResourceRevision: 1, FixedReplicas: 1, Ingress: null, DeploymentRevision: 1,
            DesiredOperation: ResourceDeploymentDesiredOperation.Apply,
            Isolation: new ResourceDeploymentIsolationDto("Unknown", "EnforcementRequired", "EnforcementNotEstablished",
            null));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(placement, options);
        var restored = JsonSerializer.Deserialize<PlacementSummaryDto>(json, options)!;
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Prepared", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Apply", document.RootElement.GetProperty("desiredOperation").GetString());
        Assert.Equal(ResourceDeploymentStatus.Prepared, restored.Status);
        Assert.Equal(ResourceDeploymentDesiredOperation.Apply, restored.DesiredOperation);
        Assert.Equal(placement.Isolation, restored.Isolation);
        Assert.Equal(3, restored.Compute.Profile.ComputeProfileRevision);
    }
}
