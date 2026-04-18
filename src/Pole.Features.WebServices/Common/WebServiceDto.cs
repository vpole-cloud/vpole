// ReSharper disable MemberCanBePrivate.Global

using Pole.Domain.Models;
using Pole.Features.Core.Models;

namespace Pole.Features.WebServices.Common;

public sealed record WebServiceDto(
    string Id,
    string Name,
    string ResourceGroupId,
    WebServiceSpecDto Spec,
    DnsRecordShortInfoDto DnsRecordShortInfo
);
public sealed record DnsRecordShortInfoDto(
    string Id,
    string Domain
);

public sealed record WebServiceSpecDto(
    ImageInfoDto Image,
    int Replicas,
    ResourcesDto Resources,
    WebServiceConfigDto Config,
    AutoscalingSpecDto? Autoscaling,
    ProbeSpecDto Probes
);
public sealed record AutoscalingSpecDto(
    int MinReplicas,
    int MaxReplicas,
    AutoscalingMetricDto Metric
);
public sealed record AutoscalingMetricDto(
    string Type,
    int TargetAverageUtilization
);

public record ProbeSpecDto(
    ProbeConfigDto Startup,
    ProbeConfigDto Liveness,
    ProbeConfigDto Readiness
);
public record ProbeConfigDto(
    string Path,
    string Port,
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
    string TargetPort,
    string Protocol,
    string Name
);

public sealed record ImageInfoDto(
    string Server,
    string Name,
    string Tag);

public static class WebServiceDtoExtensions
{
    public static WebServiceDto ToDto(this WebService webService) => new(
        webService.Id,
        webService.Name,
        webService.ResourceGroupId,
        webService.Spec.ToDto(),
        webService.DnsRecord.ToDto()
    );
    public static DnsRecordShortInfoDto ToDto(this DnsRecordShortInfo dnsRecord) => new(
        dnsRecord.Id,
        dnsRecord.Domain
    );
    public static WebServiceSpecDto ToDto(this WebServiceSpec spec) => new(
        spec.Image.ToDto(),
        spec.Replicas,
        spec.Resources.ToDto(),
        spec.Config.ToDto(),
        spec.Autoscaling?.ToDto(),
        spec.Probes.ToDto()
    );
    public static AutoscalingSpecDto ToDto(this AutoscalingSpec autoscaling) => new(
        autoscaling.MinReplicas,
        autoscaling.MaxReplicas,
        autoscaling.Metric.ToDto()
    );
    public static AutoscalingMetricDto ToDto(this AutoscalingMetric metric) => new(
        metric.Type,
        metric.TargetAverageUtilization
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
            .Select(pm => new PortMappingDto(pm.ServicePort, pm.TargetPort, pm.Protocol, pm.Name))
            .ToList()
    );
    public static ImageInfoDto ToDto(this ImageInfo image) => new(
        image.Server,
        image.Name,
        image.Tag
    );
    
    public static WebServiceSpec ToDomain(this WebServiceSpecDto dto, string kubernetesNamespace) => new(
        kubernetesNamespace,
        dto.Image.ToDomain(),
        dto.Replicas,
        dto.Resources.ToDomain(),
        dto.Config.ToDomain(),
        dto.Autoscaling?.ToDomain(),
        dto.Probes.ToDomain()
    );
    public static AutoscalingSpec ToDomain(this AutoscalingSpecDto dto) => new(
        dto.MinReplicas,
        dto.MaxReplicas,
        dto.Metric.ToDomain()
    );
    public static AutoscalingMetric ToDomain(this AutoscalingMetricDto dto) => new(
        dto.Type,
        dto.TargetAverageUtilization
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
            .Select(pm => new PortMapping(pm.ServicePort, pm.TargetPort, pm.Protocol, pm.Name))
            .ToList()
    );
    public static ImageInfo ToDomain(this ImageInfoDto dto) => new(
        dto.Server,
        dto.Name,
        dto.Tag
    );
    public static DnsRecordShortInfo ToDomain(this DnsRecordShortInfoDto shortInfoDto) => new(
        shortInfoDto.Id,
        shortInfoDto.Domain
    );
}
