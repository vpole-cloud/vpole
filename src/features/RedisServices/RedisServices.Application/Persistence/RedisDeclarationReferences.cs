using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.StorageProfiles.Contracts;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using static Pole.Features.RedisServices.Application.Persistence.RedisDeclarations;
using static Pole.Features.RedisServices.Application.Persistence.RedisPlanning;

namespace Pole.Features.RedisServices.Application.Persistence;

internal sealed class RedisDeclarationReferences(
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveComputeName,
    ResolveNamedStorageProfile resolveStorageName,
    ResolveComputeProfilePlanningSnapshot loadCompute,
    ResolveStorageProfilePlanningSnapshot loadStorage)
{
    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken ct)
    {
        var spec = DeclarationJson.Read<RedisDeclaration>(operation.Declaration.Spec);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var storageAddress = new ResourceAddress("StorageProfile", spec.Storage.Profile.Name);
        var pinned = await transactions.InvokeOutcomeAsync<RedisPlanningReferences>(async (tx, token) =>
        {
            var target = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, targetAddress),
                tx.Session, token);
            var compute = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, computeAddress),
                tx.Session, token);
            var storage = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, storageAddress),
                tx.Session, token);
            return tx.Commit(new RedisPlanningReferences(target, compute, storage));
        }, ct);

        var targetId = pinned.TargetId;
        if (targetId is null)
        {
            var found = await resolveTargetName(new(operation.Context.TenantId,
                RedisResourceDeployment.ResourceType, targetAddress.Name), ct);
            if (found is not DeploymentTargetLookupDto target)
                return Blocked(RedisDeclarationError.DeploymentTargetUnavailable);

            var result = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
                new(targetAddress, target.Id)), ct);
            if (result is StaleGitExecution stale)
                return stale;

            targetId = ((GitReferencePinned)result.Value).ResourceId;
        }

        if (pinned.ComputeId is null)
        {
            var found = await resolveComputeName(new(operation.Context.TenantId,
                RedisResourceDeployment.ResourceType, computeAddress.Name, targetId,
                spec.Placement.ComputeProfile.Revision), ct);
            if (found is not ResolvedComputeProfile compute)
                return Blocked(RedisDeclarationError.ComputeProfileUnavailable);

            var result = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
                new(computeAddress, compute.ComputeProfileId)), ct);
            if (result is StaleGitExecution stale)
                return stale;
        }

        if (pinned.StorageId is null)
        {
            var found = await resolveStorageName(new(operation.Context.TenantId,
                RedisResourceDeployment.ResourceType, storageAddress.Name, targetId,
                spec.Storage.SizeGb, spec.Storage.Profile.Revision), ct);
            if (found is not ResolvedStorageProfile storage)
                return Blocked(RedisDeclarationError.StorageProfileUnavailable);

            var result = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
                new(storageAddress, storage.ProfileId)), ct);
            if (result is StaleGitExecution stale)
                return stale;
        }

        return new DeclarationReferencesPrepared();
    }

    private sealed record RedisPlanningReferences(
        string? TargetId,
        string? ComputeId,
        string? StorageId);

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

    private static bool Matches(IReadOnlyList<GitPreviewResolvedReference> references, ResourceAddress address,
        string id) => references.All(item => item.Address != address || item.ResourceId == id);
}
