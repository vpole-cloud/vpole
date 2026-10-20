using System.Globalization;
using System.Text.Json;

namespace Pole.Tests.E2E.Infrastructure;

internal static class NativeDeploymentReadiness
{
    public static bool IsCurrent(
        JsonElement deployment,
        string image,
        long resourceRevision,
        long desiredGeneration,
        JsonElement? configMap = null,
        IReadOnlyDictionary<string, string>? expectedEnvironment = null)
    {
        var spec = deployment.GetProperty("spec");
        var template = spec.GetProperty("template");
        if (!template.GetProperty("metadata").TryGetProperty("annotations", out var annotations) ||
            !Matches(annotations, "pole.server/revision", resourceRevision) ||
            !Matches(annotations, "pole.server/deploymentGeneration", desiredGeneration)) return false;
        var container = template.GetProperty("spec").GetProperty("containers")[0];
        if (container.GetProperty("image").GetString() != image) return false;
        if (expectedEnvironment is not null)
        {
            if (configMap is not { } currentConfigMap) return false;
            var metadata = deployment.GetProperty("metadata");
            var configMetadata = currentConfigMap.GetProperty("metadata");
            var expectedConfigMapName = $"{metadata.GetProperty("name").GetString()}-config";
            if (configMetadata.GetProperty("name").GetString() != expectedConfigMapName ||
                configMetadata.GetProperty("namespace").GetString() != metadata.GetProperty("namespace").GetString() ||
                !container.TryGetProperty("envFrom", out var sources) ||
                !sources.EnumerateArray().Any(source => source.TryGetProperty("configMapRef", out var reference) &&
                    reference.GetProperty("name").GetString() == expectedConfigMapName) ||
                !currentConfigMap.TryGetProperty("data", out var data)) return false;
            foreach (var (name, value) in expectedEnvironment)
                if (!data.TryGetProperty(name, out var actual) || actual.GetString() != value) return false;
        }
        var generation = deployment.GetProperty("metadata").GetProperty("generation").GetInt64();
        var desired = spec.GetProperty("replicas").GetInt32();
        if (!deployment.TryGetProperty("status", out var status) ||
            !status.TryGetProperty("observedGeneration", out var observed) || observed.GetInt64() < generation) return false;
        return new[] { "replicas", "updatedReplicas", "readyReplicas", "availableReplicas" }.All(name =>
            status.TryGetProperty(name, out var replicas) && replicas.GetInt32() == desired);
    }

    private static bool Matches(JsonElement annotations, string name, long expected) =>
        annotations.TryGetProperty(name, out var value) &&
        long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var actual) && actual == expected;
}
