using System.Globalization;
using System.Text.Json;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class NativeDeploymentReadinessTests
{
    private static readonly Dictionary<string, string> ExpectedEnvironment = new() { ["UI_REVIEW"] = "true" };

    [Fact]
    public void Current_rollout_with_literals_from_the_owned_config_map_is_ready() => Assert.True(
        NativeDeploymentReadiness.IsCurrent(Deployment(), "nginx:tested", 2, 3, ConfigMap(), ExpectedEnvironment));

    [Theory]
    [InlineData(6, 1, 1, 1, 1, false)] // Old native generation is still ready.
    [InlineData(7, 2, 1, 1, 1, false)] // Previous ReplicaSet still has a Pod.
    [InlineData(7, 1, 0, 1, 1, false)]
    [InlineData(7, 1, 1, 0, 1, false)]
    [InlineData(7, 1, 1, 1, 0, false)]
    [InlineData(7, 1, 1, 1, 1, true)]
    public void Requires_complete_native_rollout(
        long observed,
        int replicas,
        int updated,
        int ready,
        int available,
        bool expected) => Assert.Equal(
        expected,
        NativeDeploymentReadiness.IsCurrent(
            Deployment(observed: observed, replicas: replicas, updated: updated, ready: ready, available: available),
            "nginx:tested",
            2,
            3,
            ConfigMap(),
            ExpectedEnvironment));

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    public void Rejects_the_previous_logical_revision_or_deployment_generation(long revision, long generation) =>
        Assert.False(
            NativeDeploymentReadiness.IsCurrent(
                Deployment(revision: revision, generation: generation),
                "nginx:tested",
                2,
                3,
                ConfigMap(),
                ExpectedEnvironment));

    [Theory]
    [InlineData("other-config", "web-tested-config", "owned", "true")]
    [InlineData("web-tested-config", "other-config", "owned", "true")]
    [InlineData("web-tested-config", "web-tested-config", "foreign", "true")]
    [InlineData("web-tested-config", "web-tested-config", "owned", "false")]
    public void Rejects_a_wrong_config_map_reference_identity_or_old_value(
        string reference,
        string name,
        string namespaceName,
        string value) => Assert.False(
        NativeDeploymentReadiness.IsCurrent(
            Deployment(reference: reference),
            "nginx:tested",
            2,
            3,
            ConfigMap(name, namespaceName, value),
            ExpectedEnvironment));

    [Fact]
    public void Requires_the_config_map_when_values_are_expected() => Assert.False(
        NativeDeploymentReadiness.IsCurrent(
            Deployment(),
            "nginx:tested",
            2,
            3,
            expectedEnvironment: ExpectedEnvironment));

    [Fact]
    public void Initial_readiness_without_expected_values_needs_no_config_map() =>
        Assert.True(NativeDeploymentReadiness.IsCurrent(Deployment(), "nginx:tested", 2, 3));

    [Fact]
    public void Still_rejects_a_different_image() => Assert.False(
        NativeDeploymentReadiness.IsCurrent(Deployment(), "nginx:previous", 2, 3, ConfigMap(), ExpectedEnvironment));

    private static JsonElement Deployment(
        long revision = 2,
        long generation = 3,
        long observed = 7,
        int replicas = 1,
        int updated = 1,
        int ready = 1,
        int available = 1,
        string reference = "web-tested-config") => JsonSerializer.SerializeToElement(
        new
        {
            metadata = new { name = "web-tested", @namespace = "owned", generation = 7 },
            spec = new
            {
                replicas = 1,
                template = new
                {
                    metadata = new
                    {
                        annotations = new Dictionary<string, string>
                        {
                            ["pole.server/revision"] = revision.ToString(CultureInfo.InvariantCulture),
                            ["pole.server/deploymentGeneration"] = generation.ToString(CultureInfo.InvariantCulture),
                        },
                    },
                    // The real mapper renders literal variables through envFrom, while Env holds resolved references.
                    spec = new
                    {
                        containers = new[]
                        {
                            new
                            {
                                image = "nginx:tested",
                                envFrom = new[] { new { configMapRef = new { name = reference } } },
                                env = Array.Empty<object>(),
                            },
                        },
                    },
                },
            },
            status = new
            {
                observedGeneration = observed, replicas, updatedReplicas = updated, readyReplicas = ready,
                availableReplicas = available,
            },
        });

    private static JsonElement ConfigMap(
        string name = "web-tested-config",
        string namespaceName = "owned",
        string value = "true") => JsonSerializer.SerializeToElement(
        new
        {
            metadata = new { name, @namespace = namespaceName },
            data = new Dictionary<string, string> { ["UI_REVIEW"] = value },
        });
}
