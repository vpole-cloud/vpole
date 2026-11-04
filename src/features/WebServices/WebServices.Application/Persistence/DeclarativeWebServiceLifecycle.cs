using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Domain;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Persistence;

public sealed record WebServiceLifecycleBuildInProgress : IDeclarationFailure;

public sealed record WebServiceLifecycleDeploymentRejected(ResourceDeploymentMutationError Error) : IDeclarationFailure;

internal sealed partial class DeclarativeWebServiceProvider
{
    public ResourceTypeId ResourceType => WebServiceResourceDeployment.ResourceType;

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await services.Find(session,
            WebServiceFilters.ById(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (request.Resource.Address.Kind != Kind || current.Name != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var loaded = await loadPlanningPlacements(new(request.TenantId, ResourceType, current.Id), session,
            cancellationToken);
        var placements = loaded is ResourceDeploymentPlanningSnapshot found ? found.Items : [];
        return new GitLifecycleResourceSnapshot(request.Resource, new(LifecycleVersions(current, placements)),
            current.Lifecycle is DeletingWebServiceLifecycle
                ? GitLifecyclePresence.Deleting : GitLifecyclePresence.Active,
            GitLifecycleProtection.ConfigurationOnly, GitLifecycleVersionSemantics.ManagedConfiguration);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var tenant = request.Authority.TenantId;
        var current = await services.Find(session, WebServiceFilters.ById(tenant, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceRemoved();
        if (current.Name != request.Resource.Address.Name || request.Resource.Address.Kind != Kind)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var loaded = await loadPlanningPlacements(new(tenant, ResourceType, current.Id), session, cancellationToken);
        var placements = loaded is ResourceDeploymentPlanningSnapshot found ? found.Items : [];
        if (!SameLifecycleVersions(LifecycleVersions(current, placements), request.Expected.Documents))
            return new DeclarationPreconditionsChanged();

        if (!await git.AuthorizeAsync(new(tenant, Kind, current.Id, Lifecycle: request.Authority,
                LifecycleEffect: GitLifecycleMutationEffect.Remove),
            session, cancellationToken))
            return new StaleGitExecution();
        if (current.Lifecycle is DeletingWebServiceLifecycle)
            return new GitLifecycleRemovalRequested();
        if (placements.Count == 0 || placements.Any(item => item.State == ResourceDeploymentPlanningState.Deleting))
            return PlanningBlocked(WebServiceDeclarationError.PlacementUnavailable);

        var scope = await lifecycleGuard.ReadScopeAsync(request.Authority, session, cancellationToken);
        if (scope is StaleGitExecution stale)
            return stale;
        var approved = ((GitLifecycleApprovedScope)scope.Value).Resources;
        if (current.Spec.Image.SourceBuildConfigurationId is { } configurationId)
        {
            var configuration = approved.SingleOrDefault(item =>
                item.Resource.Address.Kind == "SourceBuildConfiguration" &&
                item.Resource.ResourceId == configurationId &&
                item.Action is GitLifecycleAction.Remove or GitLifecycleAction.Replace);
            if (configuration is null)
                return new DeclarationReadBlocked(new GitLifecycleDependencyRetained());

            var actual = await loadBuildConfiguration(new(tenant, configurationId), session, cancellationToken);
            if (actual is SourceBuildConfigurationSnapshot existing && (configuration.Expected is null ||
                    !configuration.Expected.Documents.Any(item =>
                        item.DocumentId == $"SourceBuildConfiguration/{configurationId}" &&
                        item.Revision == existing.Revision)) ||
                actual is not SourceBuildConfigurationSnapshot && configuration.Expected is not null)
                return new DeclarationPreconditionsChanged();
        }

        var connections = await loadCascadePlanning(new(tenant, ResourceType, current.Id), session, cancellationToken);
        foreach (var connection in connections)
        {
            var item = approved.SingleOrDefault(item => item.Resource.Address.Kind == "ResourceConnection" &&
                item.Resource.ResourceId == connection.ConnectionId &&
                item.Action is GitLifecycleAction.Remove or GitLifecycleAction.Replace);
            if (item?.Expected is null)
                return new DeclarationReadBlocked(new GitLifecycleDependencyRetained());
            if (ResourceConnectionPlanningVersion.FromDocuments(connection.ConnectionId,
                item.Expected.Documents.ToDictionary(item => item.DocumentId, item => item.Revision)) != connection)
                return new DeclarationPreconditionsChanged();
        }

        var quota = WebServiceQuotaInput.Create(placements.Select(item => WebServiceDeploymentQuotaInput.Create(
            item.Compute.Requests.CpuMillis, item.Compute.Requests.MemoryMi,
            current.Spec.Autoscaling?.MaxReplicas ?? item.FixedReplicas)));
        await releaseQuota.ReleaseAsync(new ReleaseQuotaUsageRequest<WebServiceQuotaInput>(tenant, quota),
            session, cancellationToken);
        var result = await deleteWorkflow(new(current,
            new(tenant, ResourceType, current.Id, current.ActiveRevision, placements.Select(item =>
                new ResourceDeploymentSetDeletionItem(item.DeploymentId, item.Revision,
                    new(item.Compute.ComputeProfileId, item.Compute.ComputeProfileRevision))).ToArray()),
            request.Authority, connections), session, cancellationToken);
        return result switch
        {
            WebServiceDeletionApplied => new GitLifecycleRemovalRequested(),
            WebServiceUpdateConflict { Reason: WebServiceUpdateConflictReason.SecretBindingsRequireRemoval } =>
                new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
            WebServiceUpdateConflict { Reason: WebServiceUpdateConflictReason.GitOwnedConfiguration } =>
                new StaleGitExecution(),
            WebServiceUpdateConflict => new DeclarationPreconditionsChanged(),
            WebServiceHostnameAttachmentRemovalRequired =>
                new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
            SourceBuildWebServiceHasActiveJobs => new DeclarationReadBlocked(new WebServiceLifecycleBuildInProgress()),
            ResourceDeploymentMutationError error =>
                new DeclarationReadBlocked(new WebServiceLifecycleDeploymentRejected(error))
        };
    }

    private static DeclarationDocumentVersion[] LifecycleVersions(WebService current,
        IReadOnlyList<ResourceDeploymentPlanningItem> placements) =>
    [
        new($"WebService/{current.Id}", current.Revision),
        new($"WebService/{current.Id}/PlanningFence", current.PlanningFence),
        new($"WebService/{current.Id}/Managed/{LifecycleConfigurationHash(current, placements)}", 0),
        .. placements.SelectMany(item => LifecyclePlacementVersions(item,
            current.Lifecycle is DeletingWebServiceLifecycle))
    ];

    private static string LifecycleConfigurationHash(WebService current,
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

    private static IReadOnlyList<DeclarationDocumentVersion> LifecyclePlacementVersions(
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

    private static bool SameLifecycleVersions(IEnumerable<DeclarationDocumentVersion> current,
        IEnumerable<DeclarationDocumentVersion> expected) =>
        current.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}
