using Pole.Server.Models;

// ReSharper disable MemberCanBePrivate.Global

namespace Pole.Server.Api.WebServices.Common;

public sealed record WebServiceDto(
    string Name,
    string ResourceGroupId,
    WebServiceSpecDto Spec,
    DnsRecordDto DnsRecord
);
public sealed record DnsRecordDto(
    string Id,
    string Domain,
    string Target
);

public sealed record WebServiceSpecDto(
    ImageInfoDto Image,
    int Replicas,
    int CpuCores,
    int MemoryMi,
    WebServiceConfigDto Config
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
        dnsRecord.Domain,
        dnsRecord.Target
    );
    public static WebServiceSpecDto ToDto(this WebServiceSpec spec) => new(
        spec.Image.ToDto(),
        spec.Replicas,
        spec.CpuCores,
        spec.MemoryMi,
        spec.Config.ToDto()
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
        dto.CpuCores,
        dto.MemoryMi,
        dto.Config.ToDomain()
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
        dto.Domain,
        dto.Target
    );
}