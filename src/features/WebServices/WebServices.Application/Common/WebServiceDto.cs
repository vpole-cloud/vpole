// ReSharper disable MemberCanBePrivate.Global
using System.Collections.Immutable;
using MongoDB.Bson;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Request;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

public sealed record WebServiceDto(
    string Id,
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    long ActiveRevision,
    string Lifecycle,
    WebServiceSpecDto Spec,
    WebServiceExposureDto Exposure,
    IReadOnlyList<PlacementSummaryDto> Deployments,
    ImmutableHashSet<string> Tags,
    GitResourceStatus? GitOps = null
);

public sealed record WebServiceReleaseDto(
    string Id,
    long ResourceRevision,
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
    ImmutableList<WebServiceReleaseDeploymentDto> Deployments,
    WebServiceSpecDto Spec
);

public sealed record WebServiceReleaseDeploymentDto(
    string ResourceDeploymentId,
    string DeploymentTargetId,
    long ResourceRevision,
    long DeploymentGeneration,
    string Operation,
    string Outcome,
    DateTime? CompletedAtUtc,
    string? FailureCode,
    string? FailureMessage
);

public sealed record WebServiceReleaseHistoryDto(
    long ActiveRevision,
    PaginatedResponse<WebServiceReleaseDto> Releases
);

public sealed record WebServiceReleaseProvenanceDto(
    string Source,
    string? CommitSha,
    string? BuildUrl,
    string? ImageDigest,
    string? PreviousImageTag,
    string NewImageTag,
    string ActorType,
    string? DeploymentTokenId,
    string? DeploymentTokenName,
    string? BuildJobId = null,
    string? SourceRepositoryId = null,
    string? GitOpsSourceId = null,
    string? GitOpsRunId = null
);

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
    string? LastUsedSource
);

public sealed record CreateWebServiceDeploymentTokenResponse(
    WebServiceDeploymentTokenDto Token,
    string RawToken
);

public sealed record WebServiceExposureDto(
    string Mode,
    string GeneratedHostnameLabelPrefix
);

public sealed record WebServiceSpecDto(
    ImageInfoDto? Image,
    WebServiceConfigDto Config,
    AutoscalingSpecDto? Autoscaling,
    ProbeSpecDto Probes,
    WebServiceImageAuthorityDto? ImageAuthority = null
);

public sealed record WebServiceImageAuthorityDto(
    string Mode,
    string? ConfigurationId,
    string? JobId,
    long? Generation,
    string? Digest
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

public sealed record WebServiceConfigDto
{
    public required ImmutableDictionary<string, string> EnvironmentVariables { get; init; }
    public required ImmutableList<PortMappingDto> PortMappings { get; init; }

    public ImmutableDictionary<string, ConnectionEnvironmentReferenceDto> ConnectionEnvironmentReferences { get; init; }
        = ImmutableDictionary<string, ConnectionEnvironmentReferenceDto>.Empty;

    public ImmutableDictionary<string, SecretEnvironmentReferenceDto> SecretEnvironmentReferences { get; init; }
        = ImmutableDictionary<string, SecretEnvironmentReferenceDto>.Empty;
}

public sealed record ConnectionEnvironmentReferenceDto(
    string ConnectionId,
    string ValueKind,
    string? ConfigurationKey = null
);

public sealed record SecretEnvironmentReferenceDto(
    string SecretId,
    string Key
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
    string Tag
);

public static class WebServiceDtoExtensions
{
    public static WebServiceDto ToDto(
        this WebService webService,
        IReadOnlyList<PlacementSummaryDto>? deployments = null) => new(
        webService.Id,
        webService.Name,
        webService.ResourceGroupId,
        webService.IsolationScopeId,
        webService.ActiveRevision,
        webService.Lifecycle switch
        {
            PreparedWebServiceLifecycle => "Prepared",
            AwaitingFirstArtifactWebServiceLifecycle => "AwaitingFirstArtifact",
            ActiveWebServiceLifecycle => "Active",
            DeletingWebServiceLifecycle => "Deleting",
        },
        webService.Spec.ToDto(),
        webService.Exposure.ToDto(),
        deployments ?? [],
        webService.Tags);

    public static WebServiceReleaseDto ToDto(this WebServiceRelease release) => new(
        release.Id,
        release.ResourceRevision,
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
        release.Deployments.Select(item => item.ToDto()).ToImmutableList(),
        release.Spec.ToDto());

    public static WebServiceReleaseDeploymentDto ToDto(this WebServiceReleaseDeployment deployment) => new(
        deployment.ResourceDeploymentId,
        deployment.DeploymentTargetId,
        deployment.ResourceRevision,
        deployment.DeploymentGeneration,
        deployment.Operation.ToString(),
        deployment.Outcome.ToString(),
        deployment.CompletedAtUtc,
        deployment.FailureCode,
        deployment.FailureMessage);

    public static WebServiceReleaseProvenanceDto ToDto(this WebServiceReleaseProvenance provenance) => provenance switch
    {
        ExternalImageTagUpdateProvenance external => new WebServiceReleaseProvenanceDto(
            external.Source,
            external.CommitSha,
            external.BuildUrl,
            external.ImageDigest,
            external.PreviousImageTag,
            external.NewImageTag,
            external.ActorType,
            external.DeploymentTokenId,
            external.DeploymentTokenName),
        SourceBuildReleaseProvenance sourceBuild => new WebServiceReleaseProvenanceDto(
            "PoleSourceBuild",
            sourceBuild.CommitSha,
            null,
            sourceBuild.ImageDigest,
            sourceBuild.PreviousImageReference,
            sourceBuild.NewImageReference,
            "SourceBuild",
            null,
            null)
        {
            BuildJobId = sourceBuild.BuildJobId,
            SourceRepositoryId = sourceBuild.SourceRepositoryId,
        },
        GitOpsReleaseProvenance gitOps => new WebServiceReleaseProvenanceDto(
            "PoleGitOps",
            gitOps.CommitSha,
            null,
            null,
            gitOps.PreviousImageTag,
            gitOps.NewImageTag,
            "GitOps",
            null,
            null)
        {
            GitOpsSourceId = gitOps.SourceId,
            GitOpsRunId = gitOps.RunId,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(provenance), provenance, null),
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
        token.LastUsedSource);

    public static WebServiceExposureDto ToDto(this WebServiceExposure exposure) => new(
        exposure.Mode.ToString(),
        exposure.GeneratedHostnameLabelPrefix);

    public static WebServiceSpecDto ToDto(this WebServiceSpec spec) => new(
        spec.Image.ResolvedImage?.ToDto(),
        spec.Config.ToDto(),
        spec.Autoscaling?.ToDto(),
        spec.Probes.ToDto(),
        spec.Image switch
        {
            ImageInfo => new WebServiceImageAuthorityDto("Direct", null, null, null, null),
            UnboundSourceBuildImage => new WebServiceImageAuthorityDto("UnboundSourceBuild", null, null, null, null),
            PendingSourceBuildImage pending => new WebServiceImageAuthorityDto(
                "PendingSourceBuild",
                pending.ConfigurationId,
                null,
                null,
                null),
            DeliveredSourceBuildImage delivered => new WebServiceImageAuthorityDto(
                "DeliveredSourceBuild",
                delivered.ConfigurationId,
                delivered.JobId,
                delivered.Generation,
                delivered.Digest),
        });

    public static AutoscalingSpecDto ToDto(this AutoscalingSpec autoscaling) => new(
        autoscaling.MinReplicas,
        autoscaling.MaxReplicas,
        autoscaling.Metric.ToDto());

    public static AutoscalingMetricDto ToDto(this AutoscalingMetric metric) => new(
        metric.Type,
        metric.TargetAverageUtilization);

    public static ProbeSpecDto ToDto(this ProbeSpec probes) => new(
        probes.Startup.ToDto(),
        probes.Liveness.ToDto(),
        probes.Readiness.ToDto());

    public static ProbeConfigDto ToDto(this ProbeConfig probe) => new(
        probe.Path,
        probe.Port,
        probe.InitialDelaySeconds,
        probe.PeriodSeconds,
        probe.TimeoutSeconds,
        probe.FailureThreshold,
        probe.SuccessThreshold);

    public static WebServiceConfigDto ToDto(this WebServiceConfig config) => new()
    {
        EnvironmentVariables = config.EnvironmentVariables,
        PortMappings = config.PortMappings
            .Select(pm => new PortMappingDto(pm.ServicePort, pm.TargetPort, pm.Protocol, pm.Name))
            .ToImmutableList(),
        ConnectionEnvironmentReferences = config.ConnectionEnvironmentReferences.ToImmutableDictionary(
            pair => pair.Key,
            pair => new ConnectionEnvironmentReferenceDto(
                pair.Value.ConnectionId,
                pair.Value.ValueKind,
                pair.Value.ConfigurationKey)),
        SecretEnvironmentReferences = config.SecretEnvironmentReferences.ToImmutableDictionary(
            pair => pair.Key,
            pair => new SecretEnvironmentReferenceDto(pair.Value.SecretId, pair.Value.Key)),
    };

    public static ImageInfoDto ToDto(this ImageInfo image) => new(image.Server, image.Name, image.Tag);

    public static WebServiceSpec ToDomain(this WebServiceSpecDto dto) => new(
        dto.Image is { } image ? image.ToDomain() : new UnboundSourceBuildImage(),
        dto.Config.ToDomain(),
        dto.Autoscaling?.ToDomain(),
        dto.Probes.ToDomain());

    public static AutoscalingSpec ToDomain(this AutoscalingSpecDto dto) => new(
        dto.MinReplicas,
        dto.MaxReplicas,
        dto.Metric.ToDomain());

    public static AutoscalingMetric ToDomain(this AutoscalingMetricDto dto) => new(
        dto.Type,
        dto.TargetAverageUtilization);

    public static ProbeSpec ToDomain(this ProbeSpecDto dto) => new(
        dto.Startup.ToDomain(),
        dto.Liveness.ToDomain(),
        dto.Readiness.ToDomain());

    public static ProbeConfig ToDomain(this ProbeConfigDto dto) => new(
        dto.Path,
        dto.Port,
        dto.InitialDelaySeconds,
        dto.PeriodSeconds,
        dto.TimeoutSeconds,
        dto.FailureThreshold,
        dto.SuccessThreshold);

    public static WebServiceConfig ToDomain(this WebServiceConfigDto dto) => new()
    {
        EnvironmentVariables = dto.EnvironmentVariables ?? ImmutableDictionary<string, string>.Empty,
        PortMappings = dto.PortMappings
            .Select(pm => new PortMapping(pm.ServicePort, pm.TargetPort, pm.Protocol, pm.Name))
            .ToImmutableList(),
        ConnectionEnvironmentReferences
            = (dto.ConnectionEnvironmentReferences ??
                ImmutableDictionary<string, ConnectionEnvironmentReferenceDto>.Empty).ToImmutableDictionary(
                pair => pair.Key,
                pair => new ConnectionEnvironmentReference(
                    ObjectId.Parse(pair.Value.ConnectionId).ToString(),
                    pair.Value.ValueKind,
                    pair.Value.ConfigurationKey)),
        SecretEnvironmentReferences
            = (dto.SecretEnvironmentReferences ?? ImmutableDictionary<string, SecretEnvironmentReferenceDto>.Empty)
            .ToImmutableDictionary(
                pair => pair.Key,
                pair => new SecretEnvironmentReference(ObjectId.Parse(pair.Value.SecretId).ToString(), pair.Value.Key)),
    };

    public static ImageInfo ToDomain(this ImageInfoDto dto) => new(dto.Server, dto.Name, dto.Tag, SecretName: null);
}
