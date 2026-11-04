using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Contracts;
using static Pole.Features.RedisServices.Application.Persistence.RedisDeclarations;
using static Pole.Features.RedisServices.Application.Persistence.RedisPlanning;

namespace Pole.Features.RedisServices.Application.Persistence;

internal sealed class RedisDeclarationPlanner(
    IMongoCollection<Redis> resources,
    IGitOpsMutations git,
    LoadIsolationScopePlanningSnapshot loadScope,
    FenceIsolationScopePlanningSnapshot fenceScope,
    ResolveDeploymentTargetPlanningSnapshot loadTarget,
    FenceDeploymentTargetPlanningSnapshot fenceTarget,
    ResolveComputeProfilePlanningSnapshot loadCompute,
    FenceComputeProfilePlanningSnapshot fenceCompute,
    ResolveStorageProfilePlanningSnapshot loadStorage,
    FenceStorageProfilePlanningSnapshot fenceStorage,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlacements)
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var current = request.Operation is { } operation
            ? await resources.Find(session, RedisFilters.ById(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(ct)
            : null;
        current ??= await resources.Find(session, RedisFilters.ByName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.Name != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        if (current.Lifecycle is DeletingRedisLifecycle)
            return Blocked(RedisDeclarationError.ResourceDeleting);

        var loaded = await loadPlacements(new(request.TenantId, RedisResourceDeployment.ResourceType, current.Id),
            session, ct);
        if (loaded is not ResourceDeploymentPlanningSnapshot { Items.Count: 1 } placements ||
            placements.Items[0].State == ResourceDeploymentPlanningState.Deleting ||
            placements.Items[0].ResourceGroupId != current.ResourceGroupId || placements.Items[0].Storage is null)
            return Blocked(RedisDeclarationError.PlacementUnavailable);

        var placement = placements.Items[0];
        var phase = request.Operation?.Phase ?? DeclarationPhase.Configure;
        return new CurrentDeclaration(current.Id,
            new([new(DeclarationVersionKeys.Resource(RedisDeclarations.Kind, current.Id), current.Revision),
                new(DeclarationVersionKeys.PlanningFence(RedisDeclarations.Kind, current.Id), current.PlanningFence),
                new(ResourceDeploymentVersionKeys.Revision(placement.DeploymentId), placement.Revision)]),
            Project(phase, new(current.ResourceGroupId, placement.IsolationScopeId,
                placement.DeploymentTargetId, current.Spec.Storage.SizeGb, current.Spec.Storage.StorageProfileId,
                placement.Storage!), placement.Compute, current.Tags),
            phase == DeclarationPhase.Prepare || current.Lifecycle is ActiveRedisLifecycle
                ? DeclarationConfigurationState.Configured : DeclarationConfigurationState.Prepared);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (request.Operation is null)
            return Blocked(RedisDeclarationError.AcceptedSnapshotRequired);

        var loaded = await ReadPlanningAsync(request.Operation, session, ct);
        if (loaded is DeclarationReadBlocked blocked)
            return blocked;

        var dependencies = (RedisPlanningDependencies)loaded.Value;
        var spec = DeclarationJson.Read<RedisDeclaration>(request.Declaration.Spec);
        return Project(request.Operation.Phase,
            new(dependencies.Scope.ResourceGroupId, dependencies.Scope.IsolationScopeId,
                dependencies.Target.Target.Id, spec.Storage.SizeGb, dependencies.Storage.Storage.ProfileId,
                StorageConfiguration(dependencies.Storage.Storage)),
            dependencies.Compute.Compute.ToResourceDeploymentConfiguration(), spec.Tags) with
        {
            Dependencies = new(DependencyVersions(dependencies))
        };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var loaded = await ReadPlanningAsync(request.Operation, session, ct);
        if (loaded is DeclarationReadBlocked blocked)
            return blocked;

        var dependencies = (RedisPlanningDependencies)loaded.Value;
        if (!DependencyVersions(dependencies).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var expectedRoot = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == DeclarationVersionKeys.Resource(RedisDeclarations.Kind, request.Operation.ResourceId));
        if (dependencies.Current is { } current)
        {
            if (expectedRoot?.Revision != current.Revision || !request.Expected.Documents.Contains(
                    new(
                        DeclarationVersionKeys.PlanningFence(RedisDeclarations.Kind, current.Id),
                        current.PlanningFence)))
                return new DeclarationPreconditionsChanged();

            var fenced = await resources.UpdateOneAsync(session, RedisFilters.AtPlanningVersion(current),
                Builders<Redis>.Update.Inc(value => value.PlanningFence, 1), cancellationToken: ct);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }
        else if (expectedRoot is not null)
        {
            return new DeclarationPreconditionsChanged();
        }

        if (await fenceScope(dependencies.Scope, session, ct) is not IsolationScopePlanningFenced ||
            await fenceTarget(dependencies.Target, session, ct) is not DeploymentTargetPlanningFenced ||
            await fenceCompute(dependencies.Compute, session, ct) is not ComputeProfilePlanningFenced ||
            await fenceStorage(dependencies.Storage, session, ct) is not StorageProfilePlanningFenced)
            return new DeclarationPreconditionsChanged();

        if (dependencies.Placements is { } placements &&
            await fencePlacements(placements, session, ct) is not ResourceDeploymentPlanningFenced)
            return new DeclarationPreconditionsChanged();

        return new DeclarationFenced();
    }

    internal async Task<ReadRedisPlanningOutcome> ReadPlanningAsync(
        GitOperationInput operation,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var spec = DeclarationJson.Read<RedisDeclaration>(operation.Declaration.Spec);
        var context = operation.Context;
        var groupId = await git.ResolveAcceptedAsync(new(context, operation.ResourceId,
            new("ResourceGroup", spec.ResourceGroup)), session, ct);
        if (groupId is null)
            return Blocked(RedisDeclarationError.ResourceGroupUnavailable);

        var scopeId = spec.Placement.IsolationScope is { } scopeName
            ? await git.ResolveAcceptedAsync(new(context, operation.ResourceId, new("IsolationScope", scopeName)),
                session, ct)
            : null;
        if (spec.Placement.IsolationScope is not null && scopeId is null)
            return Blocked(RedisDeclarationError.IsolationScopeUnavailable);

        if (await loadScope(new(context.TenantId, groupId, scopeId), session, ct)
            is not IsolationScopePlanningSnapshot scope)
            return Blocked(RedisDeclarationError.IsolationScopeUnavailable);

        var targetId = await git.ResolveAcceptedAsync(new(context, operation.ResourceId,
            new("DeploymentTarget", spec.Placement.DeploymentTarget)), session, ct);
        if (targetId is null || await loadTarget(new(context.TenantId, RedisResourceDeployment.ResourceType,
                scope.IsolationScopeId, targetId, DeploymentTargetRequiredCapabilities.None), session, ct)
            is not DeploymentTargetPlanningSnapshot target)
            return Blocked(RedisDeclarationError.DeploymentTargetUnavailable);

        var computeId = await git.ResolveAcceptedAsync(new(context, operation.ResourceId,
            new("ComputeProfile", spec.Placement.ComputeProfile.Name)), session, ct);
        if (computeId is null || await loadCompute(new(context.TenantId, RedisResourceDeployment.ResourceType,
                computeId, targetId, spec.Placement.ComputeProfile.Revision), session, ct)
            is not ComputeProfilePlanningSnapshot compute)
            return Blocked(RedisDeclarationError.ComputeProfileUnavailable);

        var storageId = await git.ResolveAcceptedAsync(new(context, operation.ResourceId,
            new("StorageProfile", spec.Storage.Profile.Name)), session, ct);
        if (storageId is null || await loadStorage(new(context.TenantId, RedisResourceDeployment.ResourceType,
                storageId, targetId, spec.Storage.SizeGb, spec.Storage.Profile.Revision), session, ct)
            is not StorageProfilePlanningSnapshot storage)
            return Blocked(RedisDeclarationError.StorageProfileUnavailable);

        var current = await resources.Find(session, RedisFilters.ById(context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(ct);
        if (current?.Lifecycle is DeletingRedisLifecycle)
            return Blocked(RedisDeclarationError.ResourceDeleting);

        ResourceDeploymentPlanningSnapshot? placements = null;
        if (current is not null)
        {
            if (await loadPlacements(new(context.TenantId, RedisResourceDeployment.ResourceType, current.Id),
                    session, ct) is not ResourceDeploymentPlanningSnapshot { Items.Count: 1 } found ||
                found.Items[0].State == ResourceDeploymentPlanningState.Deleting || found.Items[0].Storage is null)
                return Blocked(RedisDeclarationError.PlacementUnavailable);

            placements = found;
        }
        else if (operation.Phase != DeclarationPhase.Prepare)
        {
            return Blocked(RedisDeclarationError.PlacementUnavailable);
        }

        return new RedisPlanningDependencies(current, scope, target, compute, storage, placements);
    }
}
