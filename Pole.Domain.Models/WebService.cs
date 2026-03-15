namespace Pole.Domain.Models;

public record WebService(
    string Name,
    string ResourceGroupId,
    string Namespace,
    WebServiceSpec Spec,
    DnsRecordShortInfo DnsRecord
) : PoleService(Name, ResourceGroupId, Namespace);

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