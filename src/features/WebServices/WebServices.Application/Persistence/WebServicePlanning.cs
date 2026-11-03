using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using System.Text.RegularExpressions;
using static Pole.Features.WebServices.Application.Persistence.WebServiceDeclarationRules;
using Pole.Workloads;
using Pole.Database;
using Pole.Features.Domains.Contracts;
using MongoDB.Bson;
using Pole.Features.Events.Abstractions;
using Pole.Features.Quotas.Contracts;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Application.Persistence.Releases;
using System.Security.Cryptography;

namespace Pole.Features.WebServices.Application.Persistence;

internal static class WebServicePlanning
{
    internal sealed record PlanningPlacement(
        string ResourceGroupId,
        string IsolationScopeId,
        string DeploymentTargetId,
        string? SourceBuildConfigurationId = null);

    internal sealed record PlanningCompute(
        string ProfileId,
        long Revision,
        int Replicas,
        JsonElement Resolution);

    internal static DeclarationProjection Project(
        WebServiceSpec spec,
        IEnumerable<string> tags,
        string exposure,
        PlanningPlacement placement,
        PlanningCompute compute,
        PlanningIngress? ingress) => new(DeclarationJson.Write(placement with
        { SourceBuildConfigurationId = spec.Image.SourceBuildConfigurationId }), DeclarationJson.Write(new
        {
            image = ImageProjection(spec.Image),
            ports = spec.Config.PortMappings.OrderBy(port => port.Name, StringComparer.Ordinal).ToArray(),
            environment = spec.Config.EnvironmentVariables,
            secretEnvironment = spec.Config.SecretEnvironmentReferences,
            connectionEnvironment = spec.Config.ConnectionEnvironmentReferences,
            probes = spec.Probes,
            autoscaling = spec.Autoscaling,
            tags = tags.Select(tag => tag.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray(),
            exposure,
            ingress,
            compute
        }));

    internal static JsonElement ImageProjection(WebServiceImage image) => image switch
    {
        ImageInfo direct => DeclarationJson.Write(new { direct.Server, direct.Name, direct.Tag }),
        PendingSourceBuildImage or DeliveredSourceBuildImage =>
            DeclarationJson.Write(new { sourceBuildConfigurationId = image.SourceBuildConfigurationId }),
        UnboundSourceBuildImage => DeclarationJson.Write(new { awaitingConfiguration = true })
    };

    internal sealed record PlanningIngress(
        string ClassName,
        string ClusterIssuer,
        string? PublicBaseDomain);

    internal static PlanningIngress? IngressProjection(ResourceDeploymentIngressConfiguration? ingress) =>
        ingress is null ? null : new(ingress.IngressClassName, ingress.ClusterIssuerName, ingress.PublicBaseDomain);

    internal static PlanningIngress? DesiredIngress(WebServiceDeclaration spec, DeploymentTargetLookupDto target) =>
        spec.Exposure == WebServiceDeclarationRules.PublicExposure &&
        target.WorkloadConfiguration.Ingress is { } ingress
            ? new(ingress.IngressClassName, ingress.ClusterIssuerName, ingress.PublicBaseDomain) : null;

    internal static DeclarationReadBlocked PlanningBlocked(WebServiceDeclarationError reason) =>
        new(new WebServiceDeclarationRejected(reason));

    internal static DeploymentTargetRequiredCapabilities RequiredCapabilities(WebServiceDeclaration spec) =>
        spec.Exposure == WebServiceDeclarationRules.PublicExposure
            ? DeploymentTargetRequiredCapabilities.PublicIngress
            : DeploymentTargetRequiredCapabilities.None;

    internal static DeclarationDocumentVersion[] DependencyVersions(WebServicePlanningDependencies dependencies) =>
    [
        new($"ResourceGroup/{dependencies.Scope.ResourceGroupId}", dependencies.Scope.ResourceGroupRevision),
        new($"IsolationScope/{dependencies.Scope.IsolationScopeId}", dependencies.Scope.ScopeRevision),
        new($"DeploymentTarget/{dependencies.Target.Target.Id}", dependencies.Target.Target.Revision),
        new($"DeploymentTarget/{dependencies.Target.Target.Id}/WorkloadConfiguration",
            dependencies.Target.Target.WorkloadConfiguration.Revision),
        new($"ComputeProfile/{dependencies.Compute.Request.ComputeProfileId}/Policy",
            dependencies.Compute.PolicyRevision),
        .. (dependencies.Placements?.Items ?? []).Select(item =>
            new DeclarationDocumentVersion($"ResourceDeployment/{item.DeploymentId}", item.Revision)),
        .. dependencies.BuildConfiguration is { } build ? new DeclarationDocumentVersion[]
            { new($"SourceBuildConfiguration/{build.Id}", build.Revision) } : [],
        .. (dependencies.Connections?.Connections ?? []).SelectMany(item => new DeclarationDocumentVersion[]
        {
            new($"ResourceConnection/{item.ConnectionId}", item.Revision),
            new($"ResourceConnection/{item.ConnectionId}/ReferenceFence", item.ReferenceFence)
        }),
        .. (dependencies.Connections?.Destinations ?? []).Select(item =>
            new DeclarationDocumentVersion($"ResourceRegistration/{item.RegistrationId}", item.ReferenceFence)),
        .. (dependencies.Secrets?.Secrets ?? []).SelectMany(item => new DeclarationDocumentVersion[]
        {
            new($"Secret/{item.SecretId}", item.Revision),
            new($"Secret/{item.SecretId}/ReferenceFence", item.ReferenceFence)
        })
    ];

    internal static JsonElement ComputeProjection(ResourceDeploymentComputeConfiguration compute) =>
        DeclarationJson.Write(new
        {
            compute.Requests,
            compute.Limits,
            compute.Architecture,
            compute.RuntimeClassName,
            extendedResources = compute.ExtendedResources.OrderBy(item => item.Name, StringComparer.Ordinal),
            nodeSelector = compute.NodeSelector.OrderBy(item => item.Key, StringComparer.Ordinal),
            requiredNodeAffinity = compute.RequiredNodeAffinity.Select(NormalizeTerm)
                .OrderBy(item => DeclarationJson.Write(item).GetRawText(), StringComparer.Ordinal),
            preferredNodeAffinity = compute.PreferredNodeAffinity.Select(item =>
                    item with { Term = NormalizeTerm(item.Term) })
                .OrderBy(item => DeclarationJson.Write(item).GetRawText(), StringComparer.Ordinal),
            tolerations = compute.Tolerations.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ThenBy(item => item.Operator).ThenBy(item => item.Value, StringComparer.Ordinal)
                .ThenBy(item => item.Effect).ThenBy(item => item.TolerationSeconds)
        });

    internal static ResourceDeploymentNodeAffinityTerm NormalizeTerm(ResourceDeploymentNodeAffinityTerm term) =>
        term with
        {
            Expressions = term.Expressions.Select(item => item with
            { Values = item.Values.Order(StringComparer.Ordinal).ToImmutableList() })
                .OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Operator).ToImmutableList()
        };

    internal static WebServiceSpec ToSpec(
        WebServiceDeclaration value,
        ImmutableDictionary<string, SecretEnvironmentReference> references,
        WebServiceSpec? previous,
        ImmutableDictionary<string, ConnectionEnvironmentReference>? connections = null,
        string? buildConfigurationId = null) =>
        new(value.Image switch
        {
            DeclaredContainerImage image => new ImageInfo(image.Server, image.Name, image.Tag,
                previous?.Image.ResolvedImage?.SecretName),
            DeclaredSourceBuildImage when previous?.Image is { } prior &&
                prior.SourceBuildConfigurationId == buildConfigurationId &&
                prior is PendingSourceBuildImage or DeliveredSourceBuildImage => prior,
            DeclaredSourceBuildImage => WebServiceImageFactory.Pending(buildConfigurationId!)
        },
            new WebServiceConfig()
            {
                EnvironmentVariables = value.Environment,
                PortMappings = value.Ports.Select(port => new PortMapping(
                    port.ServicePort,
                    port.TargetPort,
                    port.Protocol,
                    port.Name)).ToImmutableList(),
                ConnectionEnvironmentReferences = connections ?? previous?.Config.ConnectionEnvironmentReferences ??
                    ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty,
                SecretEnvironmentReferences = references
            },
            value.Autoscaling?.ToDomain(),
            value.Probes.ToDomain());

    internal static GitPreviewResolvedReference? PreviewReference(
        IReadOnlyList<GitPreviewResolvedReference> references, ResourceAddress address) =>
        references.SingleOrDefault(item => item.Address == address);

    internal static DeclarationDocumentVersion[] LifecycleVersions(WebService current,
        IReadOnlyList<ResourceDeploymentPlanningItem> placements) =>
    [
        new($"WebService/{current.Id}", current.Revision),
        new($"WebService/{current.Id}/PlanningFence", current.PlanningFence),
        new($"WebService/{current.Id}/Managed/{LifecycleConfigurationHash(current, placements)}", 0),
        .. placements.SelectMany(item => LifecyclePlacementVersions(item,
            current.Lifecycle is DeletingWebServiceLifecycle))
    ];

    internal static string LifecycleConfigurationHash(WebService current,
        IReadOnlyList<ResourceDeploymentPlanningItem> placements)
    {
        var value = new
        {
            current.ResourceGroupId,
            current.IsolationScopeId,
            current.DeclaredPlacement,
            image = ImageProjection(current.Spec.Image),
            ports = current.Spec.Config.PortMappings.OrderBy(item => item.Name, StringComparer.Ordinal),
            environment = current.Spec.Config.EnvironmentVariables.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            secrets = current.Spec.Config.SecretEnvironmentReferences.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            connections = current.Spec.Config.ConnectionEnvironmentReferences
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            current.Spec.Probes,
            current.Spec.Autoscaling,
            tags = current.Tags.Order(StringComparer.Ordinal),
            current.Exposure.Mode,
            lifecycle = current.Lifecycle.GetType().Name,
            placements = placements.OrderBy(item => item.DeploymentId, StringComparer.Ordinal).Select(item => new
            {
                item.DeploymentId,
                item.ResourceGroupId,
                item.IsolationScopeId,
                item.DeploymentTargetId,
                item.FixedReplicas,
                compute = ComputeProjection(item.Compute),
                ingress = IngressProjection(item.Ingress)
            })
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    internal static IReadOnlyList<DeclarationDocumentVersion> LifecyclePlacementVersions(
        ResourceDeploymentPlanningItem item, bool deleting)
    {
        if (!deleting || item.State != ResourceDeploymentPlanningState.Deleting)
            return [new($"ResourceDeployment/{item.DeploymentId}/DesiredGeneration", item.DesiredGeneration),
                new($"ResourceDeployment/{item.DeploymentId}/ResourceRevision", item.ResourceRevision)];
        var intent = new
        {
            item.ResourceGroupId,
            item.IsolationScopeId,
            item.DeploymentTargetId,
            item.FixedReplicas,
            item.Compute.ComputeProfileId,
            item.Compute.ComputeProfileRevision,
            compute = ComputeProjection(item.Compute),
            item.Storage,
            ingress = item.Ingress is { } ingress ? new
            {
                ingress.IngressClassName,
                ingress.ClusterIssuerName,
                ingress.PublicBaseDomain,
                hostnames = ingress.Hostnames.Order(StringComparer.Ordinal)
            } : null,
            item.DeploymentTargetConfigurationRevision,
            item.DesiredGeneration,
            item.ResourceRevision
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent)));
        return [new($"ResourceDeployment/{item.DeploymentId}/DeleteIntent/{hash}", item.DesiredGeneration)];
    }

    internal static bool SameLifecycleVersions(IEnumerable<DeclarationDocumentVersion> current,
        IEnumerable<DeclarationDocumentVersion> expected) =>
        current.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}

internal sealed record WebServicePlanningDependencies(
    WebService? Current,
    IsolationScopePlanningSnapshot Scope,
    DeploymentTargetPlanningSnapshot Target,
    ComputeProfilePlanningSnapshot Compute,
    ResourceDeploymentPlanningSnapshot? Placements,
    ConsumerSecretPlanningSnapshot? Secrets,
    ImmutableDictionary<string, SecretEnvironmentReference> References,
    ResourceConnectionReferencePlanningSnapshot? Connections,
    ImmutableDictionary<string, ConnectionEnvironmentReference>? ConnectionReferences,
    string? BuildConfigurationId,
    SourceBuildConfigurationSnapshot? BuildConfiguration);

internal union ReadWebServicePlanningOutcome(
    WebServicePlanningDependencies,
    DeclarationReadBlocked);

internal sealed record WebServicePlanningInput(
    string TenantId,
    string ResourceId,
    CompiledDeclaration Declaration,
    DeclarationPhase Phase,
    Func<ResourceAddress, Task<string?>> Reference);
