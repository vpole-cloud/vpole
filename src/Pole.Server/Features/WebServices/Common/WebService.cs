using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.WebServices.Common;

public record WebService(
    string Name,
    string ResourceGroupId,
    WebServiceSpec Spec,
    DnsRecordShortInfo DnsRecord
    ) : BaseEntity
{
    public WebServiceStatus Status { get; init; } = new Provisioning();
    public ImmutableList<string> Tags { get; init; } = ImmutableList<string>.Empty;
    public ImmutableList<ServiceEvent> Events { get; init; } = ImmutableList<ServiceEvent>.Empty;
    public string Version { get; init; } = Guid.NewGuid().ToString("N");
}

public record ServiceEvent(
    DateTime OccurredOn,
    string Message
);

[BsonDiscriminator(RootClass =  true)]
[BsonKnownTypes(typeof(Provisioning), typeof(Running), typeof(Failed), typeof(Stopped))]
public abstract record WebServiceStatus
{
    public DateTime LastUpdatedOn { get; init; } = DateTime.UtcNow;
}

public sealed record Provisioning : WebServiceStatus;
public sealed record Running : WebServiceStatus;
public sealed record Failed(string Reason) : WebServiceStatus;
public sealed record Stopped : WebServiceStatus;


public record DnsRecordShortInfo (
    string Id,
    string Domain
);

public record WebServiceSpec(
    ImageInfo Image,
    int Replicas,
    Resources Resources,
    WebServiceConfig Config,
    AutoscalingSpec? Autoscaling,
    ProbeSpec Probes
);

public record AutoscalingSpec(
    int MinReplicas,
    int MaxReplicas,
    AutoscalingMetric Metric
);

public record AutoscalingMetric(
    string Type,
    int TargetAverageUtilization
);

public record Resources(
    Resource Requests,
    Resource Limits
);
public record Resource(
    int CpuCores,
    int MemoryMi
);
public record ProbeSpec(
    ProbeConfig Startup,
    ProbeConfig Liveness,
    ProbeConfig Readiness
);
public record ProbeConfig(
    string Path,
    string Port,
    int InitialDelaySeconds = 10,
    int PeriodSeconds = 10,
    int TimeoutSeconds = 5,
    int FailureThreshold = 3,
    int SuccessThreshold = 1);

public record WebServiceConfig( 
    Dictionary<string, string> EnvironmentVariables,
    List<PortMapping> PortMappings
);

/// <summary>
/// 
/// </summary>
/// <param name="ServicePort">Service Port</param>
/// <param name="TargetPort">Container Port</param>
/// <param name="Protocol">TCP, UDP, </param>
public record PortMapping(
    int ServicePort,
    string TargetPort,
    string Protocol,
    string Name
);

public record ImageInfo(
    string Server,
    string Name,
    string Tag = "latest",
    string? SecretName = null
)
{
    public string FullImageName => $"{Server}/{Name}:{Tag}";
}