// ReSharper disable MemberCanBePrivate.Global

namespace Pole.Server.Features.WebServices.Common;

public sealed record WebServiceDto(
    string Name,
    string ResourceGroupId,
    WebServiceSpecDto Spec,
    DnsRecordDto DnsRecord
);
public sealed record DnsRecordDto(
    string Id,
    string Domain
);

public sealed record WebServiceSpecDto(
    ImageInfoDto Image,
    int Replicas,
    ResourcesDto Resources,
    WebServiceConfigDto Config,
    ProbeSpecDto Probes
);
public record ResourcesDto(
    ResourceDto Requests,
    ResourceDto Limits
);
public record ResourceDto(
    int CpuCores,
    int MemoryMi
);
public record ProbeSpecDto(
    ProbeConfigDto Startup,
    ProbeConfigDto Liveness,
    ProbeConfigDto Readiness
);
public record ProbeConfigDto(
    string Path,
    int Port,
    int InitialDelaySeconds,
    int PeriodSeconds,
    int TimeoutSeconds,
    int FailureThreshold,
    int SuccessThreshold
);

public sealed record WebServiceConfigDto( 
    Dictionary<string, string> EnvironmentVariables,
    List<PortMappingDto> PortMappings
);

public sealed record PortMappingDto(
    int ServicePort,
    int TargetPort,
    string Protocol
);

public sealed record ImageInfoDto(
    string Server,
    string Name,
    string Tag);

public static class WebServiceDtoExtensions
{
    public static WebServiceDto ToDto(this WebService webService) => new(
        webService.Name,
        webService.ResourceGroupId,
        webService.Spec.ToDto(),
        webService.DnsRecord.ToDto()
    );
    public static DnsRecordDto ToDto(this DnsRecord dnsRecord) => new(
        dnsRecord.Id,
        dnsRecord.Domain
    );
    public static WebServiceSpecDto ToDto(this WebServiceSpec spec) => new(
        spec.Image.ToDto(),
        spec.Replicas,
        spec.Resources.ToDto(),
        spec.Config.ToDto(),
        spec.Probes.ToDto()
    );
    public static ResourceDto ToDto(this Resource resource) => new(
        resource.CpuCores,
        resource.MemoryMi
    );
    public static ResourcesDto ToDto(this Resources resources) => new(
        resources.Requests.ToDto(),
        resources.Limits.ToDto()
    );
    public static ProbeSpecDto ToDto(this ProbeSpec probes) => new(
        probes.Startup.ToDto(),
        probes.Liveness.ToDto(),
        probes.Readiness.ToDto()
    );
    
    public static ProbeConfigDto ToDto(this ProbeConfig probe) => new(
        probe.Path,
        probe.Port,
        probe.InitialDelaySeconds,
        probe.PeriodSeconds,
        probe.TimeoutSeconds,
        probe.FailureThreshold,
        probe.SuccessThreshold
    );
    public static WebServiceConfigDto ToDto(this WebServiceConfig config) => new(
        config.EnvironmentVariables,
        config.PortMappings
            .Select(pm => new PortMappingDto(pm.ServicePort, pm.TargetPort, pm.Protocol))
            .ToList()
    );
    public static ImageInfoDto ToDto(this ImageInfo image) => new(
        image.Server,
        image.Name,
        image.Tag
    );
    
    public static WebServiceSpec ToDomain(this WebServiceSpecDto dto) => new(
        dto.Image.ToDomain(),
        dto.Replicas,
        dto.Resources.ToDomain(),
        dto.Config.ToDomain(),
        dto.Probes.ToDomain()
    );
    public static Resource ToDomain(this ResourceDto dto) => new(
        dto.CpuCores,
        dto.MemoryMi
    );
    public static Resources ToDomain(this ResourcesDto dto) => new(
        dto.Requests.ToDomain(),
        dto.Limits.ToDomain()
    );
    public static ProbeSpec ToDomain(this ProbeSpecDto dto) => new(
        dto.Startup.ToDomain(),
        dto.Liveness.ToDomain(),
        dto.Readiness.ToDomain()
    );
    public static ProbeConfig ToDomain(this ProbeConfigDto dto) => new(
        dto.Path,
        dto.Port,
        dto.InitialDelaySeconds,
        dto.PeriodSeconds,
        dto.TimeoutSeconds,
        dto.FailureThreshold,
        dto.SuccessThreshold
    );
    public static WebServiceConfig ToDomain(this WebServiceConfigDto dto) => new(
        dto.EnvironmentVariables,
        dto.PortMappings
            .Select(pm => new PortMapping(pm.ServicePort, pm.TargetPort, pm.Protocol))
            .ToList()
    );
    public static ImageInfo ToDomain(this ImageInfoDto dto) => new(
        dto.Server,
        dto.Name,
        dto.Tag
    );
    public static DnsRecord ToDomain(this DnsRecordDto dto) => new(
        dto.Id,
        dto.Domain
    );
}