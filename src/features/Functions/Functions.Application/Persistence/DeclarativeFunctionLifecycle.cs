using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.Functions.Application.Persistence;

public sealed record FunctionLifecycleDeploymentRejected(ResourceDeploymentMutationError Error) : IDeclarationFailure;

internal sealed partial class DeclarativeFunctionProvider
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

    private async Task<FunctionLifecycleState> ReadLifecycleStateAsync(
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

    private static DeclarationDocumentVersion[] LifecycleVersions(FunctionLifecycleState state) =>
    [
        new($"FunctionApp/{state.Root.Id}", state.Root.NextRevisionNumber),
        new($"FunctionApp/{state.Root.Id}/PlanningFence", state.Root.PlanningFence),
        new($"FunctionApp/{state.Root.Id}/Managed/{PlanningRootHash(state.Root)}", 0),
        new($"FunctionApp/{state.Root.Id}/Placements/{LifecyclePlacementHash(state.Placements)}", 0),
        new($"FunctionApp/{state.Root.Id}/Pointers/{state.Root.ActiveRevisionId}/{state.Root.PendingRevisionId}", 0),
        new($"FunctionRoute/{state.Root.FunctionRoute}/{state.Route?.State}/{state.Route?.ActiveRevisionId}",
            state.Route?.PlanningFence ?? 0),
        .. state.Configuration.Select(item => new DeclarationDocumentVersion(
            $"FunctionRevision/{item.Id}/{ManagedSpecHash(item.Spec)}", item.PlanningFence)),
        .. state.Placements.SelectMany(item => LifecyclePlacementVersions(item,
            state.Root.DesiredState == FunctionAppDesiredState.Deleting))
    ];

    private static string LifecyclePlacementHash(IReadOnlyList<ResourceDeploymentPlanningItem> placements) =>
        Hash(DeclarationJson.Write(placements.OrderBy(item => item.DeploymentId, StringComparer.Ordinal)
            .Select(item => new
            {
                item.DeploymentId,
                item.ResourceGroupId,
                item.IsolationScopeId,
                item.DeploymentTargetId,
                item.FixedReplicas,
                item.State,
                compute = ComputeProjection(item.Compute)
            })));

    private static IReadOnlyList<DeclarationDocumentVersion> LifecyclePlacementVersions(
        ResourceDeploymentPlanningItem item,
        bool deleting)
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
        var hash = Hash(DeclarationJson.Write(intent));
        return [new($"ResourceDeployment/{item.DeploymentId}/DeleteIntent/{hash}", item.DesiredGeneration)];
    }

    private static bool SameLifecycleVersions(IEnumerable<DeclarationDocumentVersion> current,
        IEnumerable<DeclarationDocumentVersion> expected) =>
        current.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}

internal sealed record FunctionLifecycleState(
    FunctionApp Root,
    FunctionRouteBinding? Route,
    IReadOnlyList<FunctionRevision> Configuration,
    IReadOnlyList<ResourceDeploymentPlanningItem> Placements);
