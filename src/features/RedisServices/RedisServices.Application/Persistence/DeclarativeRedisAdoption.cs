using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.RedisServices.Application.Persistence;

internal sealed partial class DeclarativeRedisProvider
{
    public async Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var spec = DeclarationJson.Read<RedisDeclaration>(request.Consumer.Spec);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var storageAddress = new ResourceAddress("StorageProfile", spec.Storage.Profile.Name);
        var target = await resolveTargetName(new(request.TenantId, ResourceType, targetAddress.Name), ct);
        if (target is not DeploymentTargetLookupDto foundTarget ||
            !Matches(request.References, targetAddress, foundTarget.Id))
            return Blocked(RedisDeclarationError.DeploymentTargetUnavailable);
        var compute = await resolveComputeName(new(request.TenantId, ResourceType, computeAddress.Name,
            foundTarget.Id, spec.Placement.ComputeProfile.Revision), ct);
        if (compute is not ResolvedComputeProfile foundCompute ||
            !Matches(request.References, computeAddress, foundCompute.ComputeProfileId))
            return Blocked(RedisDeclarationError.ComputeProfileUnavailable);
        var storage = await resolveStorageName(new(request.TenantId, ResourceType, storageAddress.Name,
            foundTarget.Id, spec.Storage.SizeGb, spec.Storage.Profile.Revision), ct);
        if (storage is not ResolvedStorageProfile foundStorage ||
            !Matches(request.References, storageAddress, foundStorage.ProfileId))
            return Blocked(RedisDeclarationError.StorageProfileUnavailable);
        var computePolicy = await loadCompute(new(request.TenantId, ResourceType, foundCompute.ComputeProfileId,
            foundTarget.Id, spec.Placement.ComputeProfile.Revision), session, ct);
        if (computePolicy is not ComputeProfilePlanningSnapshot pinnedCompute)
            return Blocked(RedisDeclarationError.ComputeProfileUnavailable);
        var storagePolicy = await loadStorage(new(request.TenantId, ResourceType, foundStorage.ProfileId,
            foundTarget.Id, spec.Storage.SizeGb, spec.Storage.Profile.Revision), session, ct);
        if (storagePolicy is not StorageProfilePlanningSnapshot pinnedStorage)
            return Blocked(RedisDeclarationError.StorageProfileUnavailable);

        return new GitPreviewReferencesSnapshot(
        [
            .. request.References.Where(item => item.Address != targetAddress && item.Address != computeAddress &&
                item.Address != storageAddress),
            new(targetAddress, foundTarget.Id,
                new([new($"DeploymentTarget/{foundTarget.Id}", foundTarget.Revision),
                    new($"DeploymentTarget/{foundTarget.Id}/WorkloadConfiguration",
                        foundTarget.WorkloadConfiguration.Revision)])),
            new(computeAddress, foundCompute.ComputeProfileId,
                new([new($"ComputeProfile/{foundCompute.ComputeProfileId}/Policy", pinnedCompute.PolicyRevision)])),
            new(storageAddress, foundStorage.ProfileId,
                new([new($"StorageProfile/{foundStorage.ProfileId}/Policy", pinnedStorage.PolicyRevision)]))
        ]);
    }

    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var references = await ReadPreviewReferencesAsync(new(request.TenantId, request.Declaration,
            request.References), session, ct);
        if (references is DeclarationReadBlocked blocked)
            return blocked;
        var accepted = ((GitPreviewReferencesSnapshot)references.Value).References;
        var spec = DeclarationJson.Read<RedisDeclaration>(request.Declaration.Spec);
        string? Reference(string kind, string name) => accepted.SingleOrDefault(item =>
            item.Address == new ResourceAddress(kind, name))?.ResourceId;
        var groupId = Reference("ResourceGroup", spec.ResourceGroup);
        var scopeId = spec.Placement.IsolationScope is { } scopeName ? Reference("IsolationScope", scopeName) : null;
        if (groupId is null || spec.Placement.IsolationScope is not null && scopeId is null)
            return Blocked(RedisDeclarationError.IsolationScopeUnavailable);
        var scope = await loadScope(new(request.TenantId, groupId, scopeId), session, ct);
        if (scope is not IsolationScopePlanningSnapshot scopeState)
            return Blocked(RedisDeclarationError.IsolationScopeUnavailable);
        var current = await ReadCurrentAsync(new(request.TenantId, request.Declaration.Address), session, ct);
        if (current is DeclarationReadBlocked unavailable)
            return unavailable;
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        var target = await loadTarget(new(request.TenantId, ResourceType, scopeState.IsolationScopeId,
            Reference("DeploymentTarget", spec.Placement.DeploymentTarget)!, DeploymentTargetRequiredCapabilities.None),
            session, ct);
        if (target is not DeploymentTargetPlanningSnapshot targetState)
            return Blocked(RedisDeclarationError.DeploymentTargetUnavailable);
        var compute = await loadCompute(new(request.TenantId, ResourceType,
            Reference("ComputeProfile", spec.Placement.ComputeProfile.Name)!, targetState.Target.Id,
            spec.Placement.ComputeProfile.Revision), session, ct);
        if (compute is not ComputeProfilePlanningSnapshot computeState)
            return Blocked(RedisDeclarationError.ComputeProfileUnavailable);
        var storage = await loadStorage(new(request.TenantId, ResourceType,
            Reference("StorageProfile", spec.Storage.Profile.Name)!, targetState.Target.Id,
            spec.Storage.SizeGb, spec.Storage.Profile.Revision), session, ct);
        if (storage is not StorageProfilePlanningSnapshot storageState)
            return Blocked(RedisDeclarationError.StorageProfileUnavailable);
        var desired = Project(DeclarationPhase.Configure,
            new(scopeState.ResourceGroupId, scopeState.IsolationScopeId, targetState.Target.Id,
                spec.Storage.SizeGb, storageState.Storage.ProfileId, StorageConfiguration(storageState.Storage)),
            computeState.Compute.ToResourceDeploymentConfiguration(), spec.Tags);
        if (!JsonElement.DeepEquals(found.Projection.Identity, desired.Identity))
            return new DeclarationReadBlocked(new DeclarationReplacementRequired());
        return new GitLifecycleAdoptionPreview(found, desired with
        {
            Dependencies = new([new($"ResourceGroup/{scopeState.ResourceGroupId}", scopeState.ResourceGroupRevision),
                new($"IsolationScope/{scopeState.IsolationScopeId}", scopeState.ScopeRevision),
                .. accepted.SelectMany(item => item.Version?.Documents ?? [])])
        }, accepted);
    }

    private static bool Matches(IReadOnlyList<GitPreviewResolvedReference> references, ResourceAddress address,
        string id) => references.All(item => item.Address != address || item.ResourceId == id);
}
