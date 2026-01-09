using System.Collections.Immutable;
using k8s.Models;

namespace Pole.Server.Models;

public record WebService(
    string Name,
    string ResourceGroupId,
    WebServiceSpec Spec,
    DnsRecord DnsRecord
    ) : BaseEntity
{
    public WebServiceProvisioningStatus ProvisioningStatus { get; init; } = new Requested();
    public string Version { get; init; } = Guid.NewGuid().ToString("N");
}

public abstract record WebServiceProvisioningStatus
{
    public DateTime LastUpdatedOn { get; init; } = DateTime.UtcNow;
}

public sealed record Requested : WebServiceProvisioningStatus;
public sealed record Provisioned : WebServiceProvisioningStatus;
public sealed record Failed(ImmutableList<string> Reasons) : WebServiceProvisioningStatus;


public record DnsRecord(
    string Id,
    string Domain,
    string Target
);

public record WebServiceSpec(
    ImageInfo Image,
    int Replicas,
    int CpuCores,
    int MemoryMi,
    WebServiceConfig Config
);

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
    int TargetPort,
    string Protocol = "TCP"
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