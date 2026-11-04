using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

public sealed record ResourceDeploymentOperationCounts(
    long Waiting,
    long Accepted,
    long Running,
    long Applied,
    long Failed);

public sealed record ResourceDeploymentOperationsPage(
    IReadOnlyList<ResourceDeploymentOperationDto> Items,
    long TotalCount,
    int PageNumber,
    int PageSize,
    ResourceDeploymentOperationCounts Counts,
    DateTime CheckedAtUtc);

public sealed class ResourceDeploymentOperationsPersistence(
    IMongoCollection<ResourceDeployment> deployments,
    LoadRegisteredResource loadResource,
    LookupDeploymentTarget lookupTarget,
    TimeProvider clock)
{
    public async Task<ResourceDeployment?> LoadAsync(string tenantId, string deploymentId, CancellationToken cancellationToken) =>
        await deployments.Find(ResourceDeploymentFilters.ForTenantDeployment(tenantId, deploymentId)).FirstOrDefaultAsync(cancellationToken);

    public async Task<ResourceDeploymentOperationsPage> PageAsync(
        string tenantId,
        string? groupId,
        string? resourceType,
        string? resourceId,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var filter = ResourceDeploymentFilters.ForOperations(tenantId, groupId, resourceType, resourceId);
        var count = await deployments.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var rows = await deployments.Find(filter).SortByDescending(deployment => deployment.UpdatedAtUtc).ThenBy(deployment => deployment.Id)
            .Skip((page - 1) * size).Limit(size).ToListAsync(cancellationToken);
        var items = new List<ResourceDeploymentOperationDto>();
        var owners = new Dictionary<(string Type, string Id), RegisteredResource?>();
        var targets = new Dictionary<string, DeploymentTargetLookupDto?>();
        foreach (var deployment in rows)
        {
            var key = (deployment.ResourceType, deployment.ResourceId);
            if (!owners.TryGetValue(key, out var owner))
            {
                owner = await loadResource(
                    new LoadRegisteredResourceRequest(
                        TenantId: tenantId,
                        ResourceTypeId: ResourceTypeId.Create(deployment.ResourceType),
                        ResourceId: deployment.ResourceId),
                    cancellationToken: cancellationToken);
                owners[key] = owner;
            }
            var targetId = deployment.ResolvedPlacement.DeploymentTargetId;
            if (!targets.TryGetValue(targetId, out var target))
            {
                target = (await lookupTarget(targetId, cancellationToken)) switch
                {
                    DeploymentTargetLookupDto found => found,
                    DeploymentTargetLookupError => null
                };
                targets[targetId] = target;
            }
            items.Add(ToOperation(deployment,
                owner?.TenantId == tenantId
                && owner.ResourceGroupId == deployment.ResourceGroupId ? owner.DisplayName : null,
                ResourceDeploymentIsolationProjection.FromPolicy(target?.NetworkPolicy
                    ?? DeploymentTargetNetworkPolicyDto.Unknown))
                with
            { OperationGeneration = deployment.State.Generation, TargetDisplayName = target?.DisplayName });
        }
        var counts = new ResourceDeploymentOperationCounts(
            await CountAsync<WaitingForRuntimeCellResourceDeploymentState>(
                ) + await CountAsync<PreparedResourceDeploymentState>(),
            await CountAsync<PendingResourceDeploymentState>(),
            await CountAsync<ReconcilingResourceDeploymentState>(),
            await CountAsync<AppliedResourceDeploymentState>(),
            await CountAsync<FailedResourceDeploymentState>());
        return new ResourceDeploymentOperationsPage(items, count, page, size, counts, clock.GetUtcNow().UtcDateTime);

        Task<long> CountAsync<TState>() where TState : ResourceDeploymentState => deployments.CountDocumentsAsync(
            ResourceDeploymentFilters.ForOperationState<TState>(filter), cancellationToken: cancellationToken);
    }

    public static ResourceDeploymentOperationDto ToOperation(ResourceDeployment deployment, string? name,
        ResourceDeploymentIsolationDto isolation) => deployment.State switch
        {
            PreparedResourceDeploymentState state => new(deployment.ToDto(isolation), name, "Prepared", 0,
                deployment.CreatedAtUtc, false, null),
            WaitingForRuntimeCellResourceDeploymentState state => new(deployment.ToDto(isolation), name, "Waiting", 0,
                state.RequestedAtUtc, false, null),
            PendingResourceDeploymentState state =>
                new(deployment.ToDto(isolation), name, "Accepted", state.AttemptCount,
                state.RequestedAtUtc, false, null),
            ReconcilingResourceDeploymentState state => new(deployment.ToDto(isolation), name, "Running",
                state.AttemptCount, state.StartedAtUtc, false, null),
            AppliedResourceDeploymentState state =>
                new(deployment.ToDto(isolation), name, "Applied", 0, state.AppliedAtUtc,
                false, null),
            FailedResourceDeploymentState state => new(deployment.ToDto(isolation), name, "Failed", state.AttemptCount,
                state.FailedAtUtc, state.Retryable, state.RetryNotBeforeUtc),
            DeletedResourceDeploymentState state =>
                new(deployment.ToDto(isolation), name, "Deleted", 0, state.DeletedAtUtc,
                false, null),
            _ => throw new InvalidOperationException("Unrecognized resource deployment state.")
        };
}
