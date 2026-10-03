using System.Collections.Immutable;

namespace Pole.Features.WebServices.Events;

public sealed record WebServiceSpecSnapshot(
    WebServiceImageInfoSnapshot Image,
    WebServiceConfigSnapshot Config,
    AutoscalingSpecSnapshot? Autoscaling,
    WebServiceProbeSpecSnapshot Probes);

public sealed record WebServiceProbeSpecSnapshot(
    WebServiceProbeConfigSnapshot Startup,
    WebServiceProbeConfigSnapshot Liveness,
    WebServiceProbeConfigSnapshot Readiness);

public sealed record WebServiceProbeConfigSnapshot(
    string Path,
    string Port,
    int InitialDelaySeconds = 10,
    int PeriodSeconds = 10,
    int TimeoutSeconds = 5,
    int FailureThreshold = 3,
    int SuccessThreshold = 1);

public sealed record AutoscalingSpecSnapshot(
    int MinReplicas,
    int MaxReplicas,
    AutoscalingMetricSnapshot Metric);

public sealed record AutoscalingMetricSnapshot(
    string Type,
    int TargetAverageUtilization);

public sealed record WebServiceConfigSnapshot(
    ImmutableDictionary<string, string> EnvironmentVariables,
    ImmutableList<PortMappingSnapshot> PortMappings)
{
    public ImmutableDictionary<string, ConnectionEnvironmentReferenceSnapshot> ConnectionEnvironmentReferences { get; init; } = ImmutableDictionary<string, ConnectionEnvironmentReferenceSnapshot>.Empty;
    public ImmutableDictionary<string, SecretEnvironmentReferenceSnapshot> SecretEnvironmentReferences { get; init; } = ImmutableDictionary<string, SecretEnvironmentReferenceSnapshot>.Empty;
}

public sealed record ConnectionEnvironmentReferenceSnapshot(
    string ConnectionId,
    string ValueKind,
    string? ConfigurationKey = null);

public sealed record SecretEnvironmentReferenceSnapshot(
    string SecretId,
    string Key);

public sealed record PortMappingSnapshot(
    int ServicePort,
    string TargetPort,
    string Protocol,
    string Name);

public sealed record WebServiceImageInfoSnapshot(
    string Server,
    string Name,
    string Tag = "latest",
    string? SecretName = null)
{
    public string FullImageName => $"{Server}/{Name}:{Tag}";
}
