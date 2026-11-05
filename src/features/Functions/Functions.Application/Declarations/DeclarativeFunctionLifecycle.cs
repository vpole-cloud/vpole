using MongoDB.Driver;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;
using static Pole.Features.Functions.Application.Declarations.FunctionDeclarations;

namespace Pole.Features.Functions.Application.Declarations;

public sealed record FunctionLifecycleDeploymentRejected(ResourceDeploymentMutationError Error) : IDeclarationFailure;

internal sealed class FunctionDeclarationLifecycle(
    IMongoCollection<FunctionApp> resources,
    IMongoCollection<FunctionRevision> revisions,
    IMongoCollection<FunctionRouteBinding> routes,
    IGitOpsMutations git,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlacements,
    IQuotaUsageRelease releaseQuota,
    IGitLifecyclePlanGuard lifecycleGuard,
    LoadResourceConnectionCascadePlanning loadCascadePlanning,
    ApplyFunctionAppDeletion deleteWorkflow)
{
    public ResourceTypeId ResourceType => FunctionResourceDeployment.ResourceType;

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await resources.Find(session,
            FunctionAppFilters.ByTenantAndId(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();
        if (request.Resource.Address.Kind != Kind || current.NormalizedName != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var state = await ReadLifecycleStateAsync(current, session, cancellationToken);
        return new GitLifecycleResourceSnapshot(request.Resource, new(LifecycleVersions(state)),
            current.DesiredState == FunctionAppDesiredState.Deleting
                ? GitLifecyclePresence.Deleting : GitLifecyclePresence.Active,
            GitLifecycleProtection.ConfigurationOnly, GitLifecycleVersionSemantics.ManagedConfiguration);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var tenant = request.Authority.TenantId;
        var current = await resources.Find(session,
            FunctionAppFilters.ByTenantAndId(tenant, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceRemoved();
        if (request.Resource.Address.Kind != Kind || current.NormalizedName != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var state = await ReadLifecycleStateAsync(current, session, cancellationToken);
        if (!SameLifecycleVersions(LifecycleVersions(state), request.Expected.Documents))
            return new DeclarationPreconditionsChanged();
        if (!await git.AuthorizeAsync(new(tenant, Kind, current.Id, Lifecycle: request.Authority,
                LifecycleEffect: GitLifecycleMutationEffect.Remove), session, cancellationToken))
            return new StaleGitExecution();
        if (current.DesiredState == FunctionAppDesiredState.Deleting)
            return new GitLifecycleRemovalRequested();
        if (state.Placements is not [var placement] ||
            placement.DeploymentId != current.Placement.ResourceDeploymentId ||
            placement.DeploymentTargetId != current.Placement.DeploymentTargetId ||
            placement.ResourceGroupId != current.ResourceGroupId ||
            placement.State == ResourceDeploymentPlanningState.Deleting)
            return Blocked(FunctionDeclarationError.PlacementUnavailable);

        var scope = await lifecycleGuard.ReadScopeAsync(request.Authority, session, cancellationToken);
        if (scope is StaleGitExecution stale)
            return stale;
        var approved = ((GitLifecycleApprovedScope)scope.Value).Resources;
        var connections = await loadCascadePlanning(new(tenant, ResourceType, current.Id), session, cancellationToken);
        foreach (var connection in connections)
        {
            var reviewed = approved.SingleOrDefault(item => item.Resource.Address.Kind == "ResourceConnection" &&
                item.Resource.ResourceId == connection.ConnectionId &&
                item.Action is GitLifecycleAction.Remove or GitLifecycleAction.Replace);
            if (reviewed?.Expected is null)
                return new DeclarationReadBlocked(new GitLifecycleDependencyRetained());
            if (ResourceConnectionPlanningVersion.FromDocuments(connection.ConnectionId,
                    reviewed.Expected.Documents.ToDictionary(item => item.DocumentId, item => item.Revision))
                != connection)
                return new DeclarationPreconditionsChanged();
        }

        await releaseQuota.ReleaseAsync(new ReleaseQuotaUsageRequest<FunctionQuotaAllocation>(tenant,
            current.AdmittedQuota), session, cancellationToken);
        var result = await deleteWorkflow(new(current, new(tenant, ResourceType, current.Id,
            placement.DeploymentId, placement.Revision,
            new(placement.Compute.ComputeProfileId, placement.Compute.ComputeProfileRevision),
            ResourceRevision: current.NextRevisionNumber), request.Authority, connections), session, cancellationToken);
        return result switch
        {
            FunctionAppDeletionApplied => new GitLifecycleRemovalRequested(),
            FunctionSecretBindingRemovalRequired => new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
            FunctionAppGitOwnedConfiguration => new StaleGitExecution(),
            FunctionAppDeletionChanged => new DeclarationPreconditionsChanged(),
            ResourceDeploymentMutationError rejected =>
                new DeclarationReadBlocked(new FunctionLifecycleDeploymentRejected(rejected))
        };
    }

    internal async Task<FunctionLifecycleState> ReadLifecycleStateAsync(
        FunctionApp current,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var route = await routes.Find(session, FunctionRouteBindingFilters.ByFunctionApp(current))
            .FirstOrDefaultAsync(cancellationToken);
        var referenced = new[] { current.ActiveRevisionId, current.PendingRevisionId }.OfType<string>().ToArray();
        var configuration = await revisions.Find(session,
            FunctionRevisionFilters.ReferencedConfiguration(current, referenced)).ToListAsync(cancellationToken);
        var placements = await loadPlacements(new(current.TenantId, ResourceType, current.Id), session,
            cancellationToken);
        return new(current, route, configuration,
            placements is ResourceDeploymentPlanningSnapshot found ? found.Items : []);
    }


    public string DependencyOwner => "Functions";

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var result = new List<GitLifecycleDependency>();
        using var cursor = await resources.Find(session, FunctionAppFilters.ByTenant(request.TenantId))
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var root in cursor.Current)
            {
                var state = await ReadLifecycleStateAsync(root, session, cancellationToken);
                var references = request.Resource.Address.Kind switch
                {
                    "ResourceGroup" => root.ResourceGroupId == request.Resource.ResourceId,
                    "IsolationScope" => state.Placements.Any(item =>
                        item.IsolationScopeId == request.Resource.ResourceId),
                    "Secret" => state.Configuration.SelectMany(item => item.Spec.Configuration.SecretReferences.Values)
                        .Any(item => item.SecretId == request.Resource.ResourceId),
                    _ => false
                };
                if (references)
                    result.Add(new(new(new(Kind, root.NormalizedName), root.Id), request.Resource,
                        new(LifecycleVersions(state))));
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
                SameLifecycleVersions(expected.Version.Documents, item.Version.Documents))))
            return new DeclarationPreconditionsChanged();

        foreach (var dependency in current)
        {
            var root = await resources.Find(session,
                FunctionAppFilters.ByTenantAndId(request.TenantId, dependency.Dependent.ResourceId))
                .FirstOrDefaultAsync(cancellationToken);
            if (root is null)
                return new DeclarationPreconditionsChanged();
            var state = await ReadLifecycleStateAsync(root, session, cancellationToken);
            var fenced = await resources.UpdateOneAsync(session, FunctionAppFilters.AtPlanningVersion(root),
                Builders<FunctionApp>.Update.Inc(item => item.PlanningFence, 1), cancellationToken: cancellationToken);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
            if (state.Placements.Count > 0 && await fencePlacements(
                    new(root.TenantId, ResourceType, root.Id, state.Placements), session, cancellationToken)
                is not ResourceDeploymentPlanningFenced)
                return new DeclarationPreconditionsChanged();

            if (state.Route is { } route)
            {
                var routeFenced = await routes.UpdateOneAsync(session,
                    FunctionRouteBindingFilters.AtPlanningVersion(route),
                    Builders<FunctionRouteBinding>.Update.Inc(item => item.PlanningFence, 1),
                    cancellationToken: cancellationToken);
                if (routeFenced.ModifiedCount != 1)
                    return new DeclarationPreconditionsChanged();
            }
            foreach (var revision in state.Configuration)
            {
                var changed = await revisions.UpdateOneAsync(session,
                    FunctionRevisionFilters.AtPlanningVersion(revision),
                    Builders<FunctionRevision>.Update.Inc(item => item.PlanningFence, 1),
                    cancellationToken: cancellationToken);
                if (changed.ModifiedCount != 1)
                    return new DeclarationPreconditionsChanged();
            }
        }

        return new DeclarationFenced();
    }
}

internal sealed record FunctionLifecycleState(
    FunctionApp Root,
    FunctionRouteBinding? Route,
    IReadOnlyList<FunctionRevision> Configuration,
    IReadOnlyList<ResourceDeploymentPlanningItem> Placements);
