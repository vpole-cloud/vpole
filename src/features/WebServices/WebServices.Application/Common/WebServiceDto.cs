// ReSharper disable MemberCanBePrivate.Global

using System.Collections.Immutable;
using Pole.Features.Request;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

public sealed record WebServiceDto(
    string Id,
    string Name,
    string ResourceGroupId,
    int ActiveRevision,
    WebServiceSpecDto Spec,
    DnsRecordShortInfoDto DnsRecordShortInfo
);

public sealed record WebServiceReleaseDto(
    string Id,
    int Revision,
    string Action,
    string Outcome,
    string? ActorUserId,
    string? ActorEmail,
    DateTime RequestedAtUtc,
    DateTime? CompletedAtUtc,
    string? FailureMessage,
    string? TraceId,
    string? CorrelationId,
    WebServiceReleaseProvenanceDto? Provenance,
    WebServiceSpecDto Spec
);

public sealed record WebServiceReleaseHistoryDto(
    int ActiveRevision,
    PaginatedResponse<WebServiceReleaseDto> Releases
);

public sealed record WebServiceReleaseProvenanceDto(
    string Source,
    string? CommitSha,
    string? BuildUrl,
    string? ImageDigest,
    string PreviousImageTag,
    string NewImageTag,
    string ActorType,
    string? DeploymentTokenId,
    string? DeploymentTokenName);

public sealed record WebServiceDeploymentTokenDto(
    string Id,
    string Name,
    string TokenPrefix,
    ImmutableHashSet<string> AllowedOperations,
    DateTime CreatedAtUtc,
    string? CreatedByUserId,
    string? CreatedByEmail,
    DateTime? RevokedAtUtc,
    string? RevokedByUserId,
    DateTime? LastUsedAtUtc,
    string? LastUsedSource);

public sealed record CreateWebServiceDeploymentTokenResponse(
    WebServiceDeploymentTokenDto Token,
    string RawToken);

public sealed record DnsRecordShortInfoDto(
    string Id,
    string Domain
);

public sealed record WebServiceSpecDto(
    ImageInfoDto Image,
    int Replicas,
    Resources Resources,
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
    ImmutableDictionary<string, string> EnvironmentVariables,
    ImmutableList<PortMappingDto> PortMappings
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
        webService.ActiveRevision,
        webService.Spec.ToDto(),
        webService.DnsRecord.ToDto()
    );
    public static WebServiceReleaseDto ToDto(this WebServiceRelease release) => new(
        release.Id,
        release.Revision,
        release.Action.ToString(),
        release.Outcome.ToString(),
        release.ActorUserId,
        release.ActorEmail,
        release.RequestedAtUtc,
        release.CompletedAtUtc,
        release.FailureMessage,
        release.TraceId,
        release.CorrelationId,
        release.Provenance?.ToDto(),
        release.Spec.ToDomain().ToDto()
    );
    public static WebServiceReleaseProvenanceDto ToDto(this WebServiceReleaseProvenance provenance) =>
        provenance switch
        {
            ExternalImageTagUpdateProvenance external => new(
                external.Source,
                external.CommitSha,
                external.BuildUrl,
                external.ImageDigest,
                external.PreviousImageTag,
                external.NewImageTag,
                external.ActorType,
                external.DeploymentTokenId,
                external.DeploymentTokenName),
            SourceBuildReleaseProvenance sourceBuild => new(
                "PoleSourceBuild",
                sourceBuild.CommitSha,
                null,
                sourceBuild.ImageDigest,
                sourceBuild.PreviousImageReference,
                sourceBuild.NewImageReference,
                "SourceBuild",
                null,
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(provenance), provenance, null)
        };
    public static WebServiceDeploymentTokenDto ToDto(this WebServiceDeploymentToken token) => new(
        token.Id,
        token.Name,
        token.TokenPrefix,
        token.AllowedOperations,
        token.CreatedAtUtc,
        token.CreatedByUserId,
        token.CreatedByEmail,
        token.RevokedAtUtc,
        token.RevokedByUserId,
        token.LastUsedAtUtc,
        token.LastUsedSource
    );
    public static DnsRecordShortInfoDto ToDto(this DnsRecordShortInfo dnsRecord) => new(
        dnsRecord.Id,
        dnsRecord.Domain
    );
    public static WebServiceSpecDto ToDto(this WebServiceSpec spec) => new(
        spec.Image.ToDto(),
        spec.Replicas,
        spec.Resources,
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
            .ToImmutableList()
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
        dto.Resources,
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
            .ToImmutableList()
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

