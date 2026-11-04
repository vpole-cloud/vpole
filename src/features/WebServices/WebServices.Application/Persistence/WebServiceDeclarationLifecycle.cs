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
using static Pole.Features.WebServices.Application.Persistence.WebServiceDeclarations;
using static Pole.Features.WebServices.Application.Persistence.WebServicePlanning;

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed class WebServiceDeclarationLifecycle(
    IMongoCollection<WebService> services,
    IGitOpsMutations git,
    LoadResourceDeploymentPlanningSnapshot loadPlanningPlacements,
    LoadSourceBuildConfigurationReference loadBuildConfiguration,
    IQuotaUsageRelease releaseQuota,
    IGitLifecyclePlanGuard lifecycleGuard,
    LoadResourceConnectionCascadePlanning loadCascadePlanning,
    ApplyWebServiceDeletion deleteWorkflow)
{
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
                        item.DocumentId == DeclarationVersionKeys.SourceBuildConfiguration(configurationId) &&
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

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var result = new List<GitLifecycleDependency>();
        using var cursor = await services.Find(session, WebServiceFilters.VisibleToTenant(request.TenantId))
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var current in cursor.Current)
            {
                if (References(current, request.Resource))
                    result.Add(new(new(new(Kind, current.Name), current.Id), request.Resource,
                        new(LifecycleVersions(current, [])), request.Resource.Address.Kind switch
                        {
                            "SourceBuildConfiguration" => GitLifecycleDependencyOrder.AfterDependentRemovalRequested,
                            "ResourceConnection" => GitLifecycleDependencyOrder.AfterRemovalRequested,
                            _ => GitLifecycleDependencyOrder.BeforeRemoval
                        }));
            }
        }

        return result.OrderBy(item => item.Dependent.ResourceId, StringComparer.Ordinal).ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await ReadDependenciesAsync(new(request.TenantId, request.Resource), session, cancellationToken);
        if (current.Count != request.Expected.Count || current.Any(item => !request.Expected.Any(expected =>
                expected.Dependent == item.Dependent && expected.Target == item.Target &&
                expected.Order == item.Order &&
                SameLifecycleVersions(expected.Version.Documents, item.Version.Documents))))
            return new DeclarationPreconditionsChanged();

        foreach (var dependency in current)
        {
            var root = await services.Find(session,
                WebServiceFilters.ById(request.TenantId, dependency.Dependent.ResourceId))
                .FirstOrDefaultAsync(cancellationToken);
            if (root is null)
                return new DeclarationPreconditionsChanged();
            var fenced = await services.UpdateOneAsync(session, WebServiceFilters.AtPlanningVersion(root),
                Builders<WebService>.Update.Inc(item => item.PlanningFence, 1),
                cancellationToken: cancellationToken);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    private static bool References(WebService current, GitLifecycleResourceReference target) =>
        target.Address.Kind switch
        {
            "ResourceGroup" => current.ResourceGroupId == target.ResourceId,
            "IsolationScope" => current.IsolationScopeId == target.ResourceId,
            "Secret" => current.Spec.Config.SecretEnvironmentReferences.Values
                .Any(item => item.SecretId == target.ResourceId),
            "ResourceConnection" => current.Spec.Config.ConnectionEnvironmentReferences.Values
                .Any(item => item.ConnectionId == target.ResourceId),
            "SourceBuildConfiguration" => current.Lifecycle is not DeletingWebServiceLifecycle &&
                current.Spec.Image.SourceBuildConfigurationId == target.ResourceId,
            _ => false
        };
}
