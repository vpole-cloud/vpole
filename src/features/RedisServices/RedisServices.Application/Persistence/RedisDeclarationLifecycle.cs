using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Quotas.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Persistence.Lifecycle;
using Pole.Features.RedisServices.Application.Persistence.Mutations;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using static Pole.Features.RedisServices.Application.Persistence.RedisDeclarations;
using static Pole.Features.RedisServices.Application.Persistence.RedisPlanning;

namespace Pole.Features.RedisServices.Application.Persistence;

internal sealed class RedisDeclarationLifecycle(
    IMongoCollection<Redis> resources,
    IGitOpsMutations git,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlacements,
    BeginRedisDeletion beginDeletion,
    RequestResourceDeploymentDeletion deletePlacement,
    IQuotaUsageRelease releaseQuota,
    IGitLifecyclePlanGuard lifecycleGuard,
    LoadResourceConnectionCascadePlanning loadCascade,
    TimeProvider clock)
{
    public string DependencyOwner => "RedisServices";

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = await resources.Find(session, RedisFilters.ById(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new GitLifecycleResourceAbsent();
        if (request.Resource.Address.Kind != Kind || current.Name != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var placements = await loadPlacements(new(request.TenantId, RedisResourceDeployment.ResourceType, current.Id),
            session, ct);
        return new GitLifecycleResourceSnapshot(request.Resource, LifecycleVersions(current, placements),
            current.Lifecycle is DeletingRedisLifecycle ? GitLifecyclePresence.Deleting : GitLifecyclePresence.Active,
            GitLifecycleProtection.ProtectedData, GitLifecycleVersionSemantics.ManagedConfiguration);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var read = await ReadLifecycleAsync(new(request.Authority.TenantId, request.Resource), session, ct);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();
        if (read is DeclarationReadBlocked blocked)
            return blocked;
        var snapshot = (GitLifecycleResourceSnapshot)read.Value;
        if (!SameLifecycleVersions(snapshot.Version, request.Expected))
            return new DeclarationPreconditionsChanged();
        if (!await git.AuthorizeAsync(new(request.Authority.TenantId, Kind, request.Resource.ResourceId,
                Lifecycle: request.Authority, LifecycleEffect: GitLifecycleMutationEffect.Remove), session, ct))
            return new StaleGitExecution();
        if (snapshot.Presence == GitLifecyclePresence.Deleting)
            return new GitLifecycleRemovalRequested();

        var scope = await lifecycleGuard.ReadScopeAsync(request.Authority, session, ct);
        if (scope is StaleGitExecution stale)
            return stale;
        var selected = ((GitLifecycleApprovedScope)scope.Value).Resources
            .Where(item => item.Resource.Address.Kind == "ResourceConnection" &&
                item.Action is GitLifecycleAction.Remove or GitLifecycleAction.Replace)
            .Select(item => ResourceConnectionPlanningVersion.FromDocuments(item.Resource.ResourceId,
                item.Expected!.Documents.ToDictionary(value => value.DocumentId, value => value.Revision,
                    StringComparer.Ordinal))).ToArray();
        if (selected.Any(item => item is null))
            return new DeclarationReadBlocked(new GitLifecycleApprovalStale());
        var affected = await loadCascade(new(request.Authority.TenantId, ResourceType, request.Resource.ResourceId),
            session, ct);
        if (affected.Any(item => !selected.Contains(item)))
            return new DeclarationReadBlocked(new GitLifecycleApprovalStale());
        var reviewed = affected;
        var current = await resources.Find(session, RedisFilters.ById(request.Authority.TenantId,
            request.Resource.ResourceId)).SingleAsync(ct);
        var placements = await loadPlacements(new(current.TenantId, RedisResourceDeployment.ResourceType, current.Id),
            session, ct);
        if (placements is not ResourceDeploymentPlanningSnapshot { Items.Count: 1 } placed)
            return Blocked(RedisDeclarationError.PlacementUnavailable);
        if (await fencePlacements(placed, session, ct) is not ResourceDeploymentPlanningFenced)
            return new DeclarationPreconditionsChanged();

        var removed = await beginDeletion(new(current, current.BeginDeletion(clock.GetUtcNow().UtcDateTime),
            request.Authority, reviewed), session, ct);
        if (removed is RedisSecretBindingRemovalRequired)
            return new DeclarationReadBlocked(new GitLifecycleDependencyRetained());
        if (removed is RedisGitOwnedConfiguration)
            return new StaleGitExecution();
        if (removed is RedisDeletionConflict)
            return new DeclarationPreconditionsChanged();

        var compute = placed.Items[0].Compute;
        await releaseQuota.ReleaseAsync(new ReleaseQuotaUsageRequest<RedisQuotaInput>(current.TenantId,
            RedisQuotaInput.Create(compute.Requests.CpuMillis, compute.Requests.MemoryMi)), session, ct);
        var placement = await deletePlacement(new(current.TenantId, RedisResourceDeployment.ResourceType, current.Id,
            new(compute.ComputeProfileId, compute.ComputeProfileRevision)), session, ct);
        return placement is PlacementSummaryDto
            ? new GitLifecycleRemovalRequested()
            : new DeclarationPreconditionsChanged();
    }

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (request.Resource.Address.Kind is not ("ResourceGroup" or "IsolationScope"))
            return [];
        var filter = RedisFilters.LifecycleDependencies(request.TenantId,
            request.Resource.Address.Kind == "ResourceGroup" ? request.Resource.ResourceId : null);
        var current = await resources.Find(session, filter).SortBy(item => item.Id).ToListAsync(ct);
        var result = new List<GitLifecycleDependency>();
        foreach (var item in current)
        {
            var placements = await loadPlacements(new(request.TenantId, RedisResourceDeployment.ResourceType, item.Id),
                session, ct);
            if (request.Resource.Address.Kind == "IsolationScope" &&
                (placements is not ResourceDeploymentPlanningSnapshot found || !found.Items.Any(placement =>
                    placement.IsolationScopeId == request.Resource.ResourceId)))
                continue;
            result.Add(new(new(new(Kind, item.Name), item.Id), request.Resource, LifecycleVersions(item, placements)));
        }
        return result;
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = await ReadDependenciesAsync(new(request.TenantId, request.Resource), session, ct);
        if (current.Count != request.Expected.Count || current.Any(item => !request.Expected.Any(expected =>
                expected.Dependent == item.Dependent && expected.Target == item.Target &&
                SameLifecycleVersions(item.Version, expected.Version))))
            return new DeclarationPreconditionsChanged();
        foreach (var item in current)
        {
            var root = await resources.Find(session, RedisFilters.ById(request.TenantId, item.Dependent.ResourceId))
                .SingleAsync(ct);
            if ((await resources.UpdateOneAsync(session, RedisFilters.AtPlanningVersion(root),
                    Builders<Redis>.Update.Inc(value => value.PlanningFence, 1),
                    cancellationToken: ct)).ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }
        return new DeclarationFenced();
    }

    private static DeclarationVersionToken LifecycleVersions(Redis resource,
        LoadResourceDeploymentPlanningSnapshotOutcome placements) => new(
        [new(DeclarationVersionKeys.Resource(RedisDeclarations.Kind, resource.Id), resource.Revision),
            new(DeclarationVersionKeys.PlanningFence(RedisDeclarations.Kind, resource.Id), resource.PlanningFence),
            .. placements is ResourceDeploymentPlanningSnapshot found
                ? found.Items.SelectMany(item => LifecyclePlacementVersions(item, resource))
                : []]);

    private static IReadOnlyList<DeclarationDocumentVersion> LifecyclePlacementVersions(
        ResourceDeploymentPlanningItem item,
        Redis resource)
    {
        if (resource.Lifecycle is not DeletingRedisLifecycle || item.State != ResourceDeploymentPlanningState.Deleting)
            return [new(DeclarationVersionKeys.DeploymentDesiredGeneration(item.DeploymentId), item.DesiredGeneration),
                new(DeclarationVersionKeys.DeploymentResourceRevision(item.DeploymentId), item.ResourceRevision)];
        var intent = new
        {
            item.ResourceGroupId,
            item.IsolationScopeId,
            item.DeploymentTargetId,
            item.FixedReplicas,
            compute = new
            {
                item.Compute.ComputeProfileId,
                item.Compute.ComputeProfileRevision,
                configuration = ComputeProjection(item.Compute)
            },
            item.Storage,
            ingress = item.Ingress is { } ingress ? new
            {
                ingress.IngressClassName,
                ingress.ClusterIssuerName,
                ingress.PublicBaseDomain,
                hostnames = ingress.Hostnames.Order(StringComparer.Ordinal)
            } : null,
            ownerGroupId = resource.ResourceGroupId,
            resource.Spec,
            tags = resource.Tags.Order(StringComparer.Ordinal),
            item.DeploymentTargetConfigurationRevision,
            item.DesiredGeneration,
            item.ResourceRevision
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent)));
        return [new(DeclarationVersionKeys.DeploymentDeleteIntent(item.DeploymentId, hash), item.DesiredGeneration)];
    }

    private static bool SameLifecycleVersions(DeclarationVersionToken left, DeclarationVersionToken right) =>
        left.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));

    public async Task<IReadOnlyList<string>> ReadLifecycleWarningsAsync(
        ReadLifecycleWarningsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Lifecycle warning reads require an active transaction.");

        if (request.Action is not (GitLifecycleAction.Remove or GitLifecycleAction.Replace) ||
            !await resources.Find(session, RedisFilters.ById(request.TenantId, request.ResourceId))
                .AnyAsync(cancellationToken))
            return [];

        // RedisInKubernetes requests native PVC deletion through StatefulSet WhenDeleted=Delete.
        return ["RedisPersistentVolumeClaimsDeleted", "PersistentVolumeReclaimPolicyDeterminesDataRetention"];
    }
}
