using System.Collections.Immutable;
using Pole.Features.Shared.Models;

namespace Pole.Features.WebServices.Events;

public sealed record WebServiceSpecSnapshot(
    WebServiceImageInfoSnapshot Image,
    int Replicas,
    WebServiceConfigSnapshot Config,
    AutoscalingSpecSnapshot? Autoscaling,
    ProbeSpec Probes);

public sealed record AutoscalingSpecSnapshot(
    int MinReplicas,
    int MaxReplicas,
    AutoscalingMetricSnapshot Metric);

public sealed record AutoscalingMetricSnapshot(
    string Type,
    int TargetAverageUtilization);

public sealed record WebServiceConfigSnapshot(
    ImmutableDictionary<string, string> EnvironmentVariables,
    ImmutableList<PortMappingSnapshot> PortMappings);

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
