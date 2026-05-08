using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Domain.Models;

public record WebService(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string Name,
    string ResourceGroupId,
    WebServiceSpec Spec,
    DnsRecordShortInfo DnsRecord,
    ServiceStatus Status,
    ImmutableHashSet<string> Tags,
    ImmutableList<ServiceEvent> Events
);

public record WebServiceSpec(
    string KubernetesNamespace,
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
    ImmutableDictionary<string, string> EnvironmentVariables,
    ImmutableList<PortMapping> PortMappings
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

public static class WebServiceFactory
{
    public sealed record CreateWebServiceOptions(
        string Id,
        ServiceStatus Status,
        ImmutableHashSet<string> Tags,
        ImmutableList<ServiceEvent> Events
    );
    
    public static CreateWebServiceOptions DefaultOptions =>
        new CreateWebServiceOptions(
            Id: ObjectId.GenerateNewId().ToString(),
            Status: new Provisioning(),
            Tags: ImmutableHashSet<string>.Empty,
            Events: ImmutableList<ServiceEvent>.Empty
        );
    
    public static WebService Create(
        string name,
        string resourceGroupId,
        WebServiceSpec spec,
        DnsRecordShortInfo dnsRecord,
        CreateWebServiceOptions options) =>
        new(
            options.Id,
            Name: name,
            ResourceGroupId: resourceGroupId,
            Spec: spec,
            DnsRecord: dnsRecord,
            Status: options.Status,
            Tags: options.Tags,
            Events: options.Events
        );
}
