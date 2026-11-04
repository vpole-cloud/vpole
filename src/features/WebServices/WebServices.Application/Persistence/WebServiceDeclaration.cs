using Pole.Features.GitOps.Primitives;
using System.Text.RegularExpressions;
using static Pole.Features.WebServices.Application.Persistence.WebServiceDeclarationRules;
using Pole.Workloads;
using Pole.Database;
using System.Collections.Immutable;
using System.Text.Json;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

public sealed record PinnedComputeProfile(
    string Name,
    long Revision);

public sealed record WebServiceDeclarationPlacement(
    string DeploymentTarget,
    PinnedComputeProfile ComputeProfile,
    string? IsolationScope = null);

public sealed record DeclaredSecretEnvironmentReference(
    string Secret,
    string Key);

public sealed record DeclaredConnectionEnvironmentReference(
    string Connection,
    string ValueKind,
    string? ConfigurationKey = null);

public sealed record WebServiceConnectionReferenceRejected : IDeclarationFailure;

public sealed record WebServiceDeclaration
{
    public required string ResourceGroup { get; init; }

    public required DeclaredWebServiceImage Image { get; init; }

    public required WebServiceDeclarationPlacement Placement { get; init; }

    public required ImmutableList<PortMappingDto> Ports { get; init; }

    public required ProbeSpecDto Probes { get; init; }

    public string Exposure { get; init; } = PrivateExposure;

    public int? Replicas { get; init; }

    public AutoscalingSpecDto? Autoscaling { get; init; }

    internal int FixedReplicas => Replicas ?? Autoscaling?.MinReplicas ?? DefaultReplicas;

    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;

    public ImmutableDictionary<string,
        DeclaredSecretEnvironmentReference> SecretEnvironment
    { get; init; } = ImmutableDictionary<string,
        DeclaredSecretEnvironmentReference>.Empty;

    public ImmutableDictionary<string, DeclaredConnectionEnvironmentReference>? ConnectionEnvironment { get; init; }

    public string[] Tags { get; init; } = [];
}

public sealed record WebServiceLifecycleBuildInProgress : IDeclarationFailure;

public sealed record WebServiceLifecycleDeploymentRejected(ResourceDeploymentMutationError Error) : IDeclarationFailure;

internal static class WebServiceDeclarations
{
    private const string ConnectionEnvironmentField = "spec.connectionEnvironment";

    private const string PortsField = "spec.ports";

    private const string ReplicasField = "spec.replicas";

    private static string ConnectionEnvironmentFieldPath(string key) => $"{ConnectionEnvironmentField}.{key}";

    public const string Kind = "WebService";
    public static ResourceTypeId ResourceType => WebServiceResourceDeployment.ResourceType;

    public static string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        WebServiceConnectionReferenceRejected => nameof(WebServiceConnectionReferenceRejected),
        WebServiceLifecycleBuildInProgress => nameof(WebServiceLifecycleBuildInProgress),
        WebServiceLifecycleDeploymentRejected => nameof(WebServiceLifecycleDeploymentRejected),
        WebServiceSecretReferenceRejected rejected => ConsumerSecretReferenceErrorCodes.Code(rejected.Error.Reason),
        WebServiceDependencyRejected rejected => WebServiceDependencyDiagnostics.Code(rejected.Error),
        WebServiceDeclarationRejected rejected => rejected.Reason switch
        {
            WebServiceDeclarationError.ExplicitAdoptionRequired => "ExplicitAdoptionRequired",
            WebServiceDeclarationError.ResourceDeleting => "ResourceDeleting",
            WebServiceDeclarationError.DeploymentTargetNameAmbiguous => "DeploymentTargetNameAmbiguous",
            WebServiceDeclarationError.DeploymentTargetUnavailable => "DeploymentTargetUnavailable",
            WebServiceDeclarationError.PreparedPlacementIdentityUnavailable => "PreparedPlacementIdentityUnavailable",
            WebServiceDeclarationError.ImmutablePlacementChange => "ImmutablePlacementChange",
            WebServiceDeclarationError.PreparedResourceUnavailable => "PreparedResourceUnavailable",
            WebServiceDeclarationError.ComputeProfileNameAmbiguous => "ComputeProfileNameAmbiguous",
            WebServiceDeclarationError.ComputeProfileRevisionUnavailable => "ComputeProfileRevisionUnavailable",
            WebServiceDeclarationError.PinnedComputeProfileUnavailable => "PinnedComputeProfileUnavailable",
            WebServiceDeclarationError.QuotaUnavailable => "QuotaUnavailable",
            WebServiceDeclarationError.ResourceGroupUnavailable => "ResourceGroupUnavailable",
            WebServiceDeclarationError.ExpectedRevisionConflict => "ExpectedRevisionConflict",
            WebServiceDeclarationError.ResourceRegistrationUnavailable => "ResourceRegistrationUnavailable",
            WebServiceDeclarationError.PlacementUnavailable => "PlacementUnavailable",
            WebServiceDeclarationError.PlacementChanged => "PlacementChanged",
            WebServiceDeclarationError.SecretUnavailable => "SecretUnavailable",
            WebServiceDeclarationError.PublicIngressUnavailable => "PublicIngressUnavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    private static readonly Regex EnvironmentName = new("^[A-Za-z_][A-Za-z0-9_]*$");

    public static DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<WebServiceDeclaration>(spec);

        return new(new("ResourceGroup", value.ResourceGroup),
            [],
            [.. value.Placement.IsolationScope is null ? Array.Empty<DeclaredReference>() :
                [new(new("IsolationScope", value.Placement.IsolationScope),
                    DeclarationFieldPaths.PlacementIsolationScope)],
            .. value.Image is DeclaredSourceBuildImage build ? new DeclaredReference[]
                { new(new("SourceBuildConfiguration", build.SourceBuildConfiguration), DeclarationFieldPaths.Image) }
                : [],
            .. (value.ConnectionEnvironment ?? []).Select(pair => new DeclaredReference(
                new("ResourceConnection", pair.Value.Connection), ConnectionEnvironmentFieldPath(pair.Key))),
            .. value.SecretEnvironment.Select(pair =>
                new DeclaredReference(
                    new("Secret", pair.Value.Secret),
                    DeclarationFieldPaths.SecretEnvironment(pair.Key),
                    pair.Value.Key,
                    RequiresAccess: true))],
            [],
            []);
    }

    public static JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<WebServiceDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(value.ResourceGroup), DeclarationFieldPaths.ResourceGroup);
        DeclarationJson.Require(value.Image switch
        {
            DeclaredContainerImage image =>
                image.Server is { Length: > 0 and <= MaximumImageComponentLength } &&
                image.Name is { Length: > 0 and <= MaximumImageComponentLength } &&
                image.Tag is { Length: > 0 and <= MaximumImageTagLength } &&
                !image.Server.Any(char.IsWhiteSpace) && !image.Name.Any(char.IsWhiteSpace) &&
                !image.Tag.Any(char.IsWhiteSpace),
            DeclaredSourceBuildImage image => DeclarationNames.Valid(image.SourceBuildConfiguration),
            null => false
        }, DeclarationFieldPaths.Image);
        DeclarationJson.Require(value.Placement is not null
                                && ResourceTechnicalName.IsValid(value.Placement.DeploymentTarget) &&
                                value.Placement.ComputeProfile is not null
                                && ResourceTechnicalName.IsValid(value.Placement.ComputeProfile.Name) &&
                                value.Placement.ComputeProfile.Revision > 0,
            DeclarationFieldPaths.Placement);
        DeclarationJson.Require(value.Placement.IsolationScope is null ||
                                ResourceTechnicalName.IsValid(value.Placement.IsolationScope) &&
                                value.Placement.IsolationScope != "default",
            DeclarationFieldPaths.PlacementIsolationScope);
        DeclarationJson.Require(value.Exposure is PrivateExposure or PublicExposure, "spec.exposure");
        DeclarationJson.Require(value.Replicas is null || value.Autoscaling is null, ReplicasField);
        DeclarationJson.Require(value.Autoscaling is null || ValidAutoscaling(value.Autoscaling), "spec.autoscaling");
        DeclarationJson.Require(
            value.FixedReplicas is >= DefaultReplicas and <= MaximumReplicas && value.Tags is not null
                                                                        && value.Tags.All(tag =>
                                                                            tag is
                                                                            {
                                                                                Length: > 0 and <= MaximumTagLength
                                                                            } && !string.IsNullOrWhiteSpace(tag)),
            ReplicasField);
        DeclarationJson.Require(value.Ports is { Count: > 0 and <= MaximumPorts } && value.Ports.All(port =>
                                    port is not null &&
                                    port.ServicePort is >= MinimumPort and <= MaximumPort
                                    && port.TargetPort is { Length: > 0 and <= MaximumPortTextLength } && int.TryParse(
                                        port.TargetPort,
                                        out var number) && number is >= MinimumPort and <= MaximumPort &&
                                    port.Protocol is TcpProtocol or UdpProtocol && port.Name is
                                    { Length: <= MaximumPortNameLength }
                                    && DeclarationNames.Valid(port.Name)) &&
                                value.Ports.Select(port => port.Name).Distinct().Count() == value.Ports.Count &&
                                value.Ports.Select(port => (port.ServicePort, port.Protocol)).Distinct()
                                    .Count() == value.Ports.Count,
            PortsField);
        DeclarationJson.Require(value.Exposure != PublicExposure ||
                                value.Ports.OrderBy(port => port.Name, StringComparer.Ordinal).First().Protocol ==
                                TcpProtocol, PortsField);
        DeclarationJson.Require(value.Probes is not null && ValidProbe(value.Probes.Startup)
                                                         && value.Probes.Startup.SuccessThreshold ==
                                                         RequiredLivenessSuccessThreshold &&
                                                         ValidProbe(value.Probes.Liveness)
                                                         && value.Probes.Liveness.SuccessThreshold ==
                                                         RequiredLivenessSuccessThreshold
                                                         && ValidProbe(value.Probes.Readiness),
            "spec.probes");
        DeclarationJson.Require(value.Environment is not null && value.SecretEnvironment is not null &&
                                value.Environment.Count + value.SecretEnvironment.Count <= MaximumEnvironmentEntries
                                && value.Environment.Values.All(item => item is not null) &&
                                value.Environment.Keys.Concat(value.SecretEnvironment.Keys)
                                    .All(name =>
                                        name.Length <= MaximumEnvironmentNameLength && EnvironmentName.IsMatch(name)) &&
                                !value.Environment.Keys.Intersect(value.SecretEnvironment.Keys).Any() &&
                                value.SecretEnvironment.Values.All(reference =>
                                    reference is not null && DeclarationNames.Valid(reference.Secret) &&
                                    SecretKeyReferenceRules.ValidKey(reference.Key)),
            DeclarationFieldPaths.Environment);

        var connections = value.ConnectionEnvironment ?? [];
        DeclarationJson.Require(connections.Count + value.Environment.Count + value.SecretEnvironment.Count <=
            MaximumEnvironmentEntries && connections.All(pair =>
                pair.Key.Length <= MaximumEnvironmentNameLength && EnvironmentName.IsMatch(pair.Key) &&
                pair.Value is not null && DeclarationNames.Valid(pair.Value.Connection) &&
                (pair.Value.ValueKind is "Host" or "Port" && pair.Value.ConfigurationKey is null ||
                 pair.Value.ValueKind == "Configuration" && !string.IsNullOrWhiteSpace(pair.Value.ConfigurationKey))) &&
            !connections.Keys.Intersect(value.Environment.Keys.Concat(value.SecretEnvironment.Keys)).Any(),
            ConnectionEnvironmentField);

        return DeclarationJson.Write(value with
        {
            Replicas = value.Autoscaling is null ? value.FixedReplicas : null,
            Ports = value.Ports.OrderBy(port => port.Name, StringComparer.Ordinal).ToImmutableList(),
            Tags = value.Tags.Select(tag => tag.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray()
        });
    }

    private static bool ValidAutoscaling(AutoscalingSpecDto value) =>
        value.MinReplicas is >= DefaultReplicas and <= MaximumReplicas &&
        value.MaxReplicas >= value.MinReplicas && value.MaxReplicas <= MaximumReplicas &&
        value.Metric is { Type: "cpu" or "memory", TargetAverageUtilization: >= 1 };

    private static bool ValidProbe(ProbeConfigDto? probe) =>
        probe is not null && probe.Path is { Length: > 0 and <= MaximumProbePathLength }
                          && probe.Path.StartsWith('/') &&
                          probe.Port is { Length: > 0 and <= MaximumPortTextLength } && int.TryParse(
                              probe.Port,
                              out var port) && port is >= MinimumPort and <= MaximumPort
                          && probe.InitialDelaySeconds is >= 0 and <= MaximumProbeInitialDelaySeconds &&
                          probe.PeriodSeconds is >= 1 and <= MaximumProbePeriodSeconds
                          && probe.TimeoutSeconds is >= 1 and <= MaximumProbeTimeoutSeconds
                          && probe.FailureThreshold is >= 1 and <= MaximumProbeThreshold
                          && probe.SuccessThreshold is >= 1 and <= MaximumProbeThreshold;

    public static IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var value = DeclarationJson.Read<WebServiceDeclaration>(spec);

        return
        [
            new(
                DeclarationPhase.Prepare,
                [
                    new(new("ResourceGroup", value.ResourceGroup), DeclarationPhase.Prepare),
                    .. value.Placement.IsolationScope is null ? Array.Empty<DeclarationDependency>() :
                        [new(new("IsolationScope", value.Placement.IsolationScope), DeclarationPhase.Prepare)]
                ]),
            new(DeclarationPhase.Configure,
            [
                new(address, DeclarationPhase.Prepare),
                .. value.Image is DeclaredSourceBuildImage build ? new DeclarationDependency[]
                    { new(new("SourceBuildConfiguration", build.SourceBuildConfiguration), DeclarationPhase.Configure) }
                    : [],
                .. (value.ConnectionEnvironment ?? []).Values.Select(reference => new DeclarationDependency(
                    new("ResourceConnection", reference.Connection), DeclarationPhase.Bind)),
                .. value.SecretEnvironment.Values.Select(reference => new DeclarationDependency(
                    new("Secret", reference.Secret),
                    DeclarationPhase.Prepare))
            ])
        ];
    }
}
