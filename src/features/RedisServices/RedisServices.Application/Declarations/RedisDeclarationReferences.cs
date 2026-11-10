using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.StorageProfiles.Contracts;
using static Pole.Features.RedisServices.Application.Declarations.RedisDeclarations;

namespace Pole.Features.RedisServices.Application.Declarations;

internal sealed class RedisDeclarationReferences(
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveComputeName,
    ResolveNamedStorageProfile resolveStorageName,
    ResolveComputeProfilePlanningSnapshot loadCompute,
    ResolveStorageProfilePlanningSnapshot loadStorage
)
{
    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken ct)
    {
        var spec = DeclarationJson.Read<RedisDeclaration>(operation.Declaration.Spec);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var storageAddress = new ResourceAddress("StorageProfile", spec.Storage.Profile.Name);
        var pinned = await transactions.InvokeOutcomeAsync<RedisPlanningReferences>(
            async (tx, token) =>
            {
                var target = await git.ResolveAcceptedAsync(
                    new ResolveAcceptedGitReferenceRequest(operation.Context, operation.ResourceId, targetAddress),
                    tx.Session,
                    token);
                var compute = await git.ResolveAcceptedAsync(
                    new ResolveAcceptedGitReferenceRequest(operation.Context, operation.ResourceId, computeAddress),
                    tx.Session,
                    token);
                var storage = await git.ResolveAcceptedAsync(
                    new ResolveAcceptedGitReferenceRequest(operation.Context, operation.ResourceId, storageAddress),
                    tx.Session,
                    token);
                return tx.Commit(new RedisPlanningReferences(target, compute, storage));
            },
            ct);

        var targetId = pinned.TargetId;
        if (targetId is null)
        {
            var found = await resolveTargetName(
                new NamedDeploymentTargetRequest(
                    operation.Context.TenantId,
                    RedisResourceDeployment.ResourceType,
                    targetAddress.Name),
                ct);
            if (found is not DeploymentTargetLookupDto target)
                return Blocked(RedisDeclarationError.DeploymentTargetUnavailable);

            var result = await git.PinReferenceAsync(
                new PinGitReferenceRequest(
                    operation.Context,
                    operation.ResourceId,
                    new GitReferenceTarget(targetAddress, target.Id)),
                ct);
            if (result is StaleGitExecution stale)
                return stale;

            targetId = ((GitReferencePinned)result.Value).ResourceId;
        }

        if (pinned.ComputeId is null)
        {
            var found = await resolveComputeName(
                new NamedComputeProfileRequest(
                    operation.Context.TenantId,
                    RedisResourceDeployment.ResourceType,
                    computeAddress.Name,
                    targetId,
                    spec.Placement.ComputeProfile.Revision),
                ct);
            if (found is not ResolvedComputeProfile compute)
                return Blocked(RedisDeclarationError.ComputeProfileUnavailable);

            var result = await git.PinReferenceAsync(
                new PinGitReferenceRequest(
                    operation.Context,
                    operation.ResourceId,
                    new GitReferenceTarget(computeAddress, compute.ComputeProfileId)),
                ct);
            if (result is StaleGitExecution stale)
                return stale;
        }

        if (pinned.StorageId is null)
        {
            var found = await resolveStorageName(
                new NamedStorageProfileRequest(
                    operation.Context.TenantId,
                    RedisResourceDeployment.ResourceType,
                    storageAddress.Name,
                    targetId,
                    spec.Storage.SizeGb,
                    spec.Storage.Profile.Revision),
                ct);
            if (found is not ResolvedStorageProfile storage)
                return Blocked(RedisDeclarationError.StorageProfileUnavailable);

            var result = await git.PinReferenceAsync(
                new PinGitReferenceRequest(
                    operation.Context,
                    operation.ResourceId,
                    new GitReferenceTarget(storageAddress, storage.ProfileId)),
                ct);
            if (result is StaleGitExecution stale)
                return stale;
        }

        return new DeclarationReferencesPrepared();
    }

    public async Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var spec = DeclarationJson.Read<RedisDeclaration>(request.Consumer.Spec);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var storageAddress = new ResourceAddress("StorageProfile", spec.Storage.Profile.Name);
        var target = await resolveTargetName(
            new NamedDeploymentTargetRequest(request.TenantId, ResourceType, targetAddress.Name),
            ct);
        if (target is not DeploymentTargetLookupDto foundTarget ||
            !Matches(request.References, targetAddress, foundTarget.Id))
            return Blocked(RedisDeclarationError.DeploymentTargetUnavailable);
        var compute = await resolveComputeName(
            new NamedComputeProfileRequest(
                request.TenantId,
                ResourceType,
                computeAddress.Name,
                foundTarget.Id,
                spec.Placement.ComputeProfile.Revision),
            ct);
        if (compute is not ResolvedComputeProfile foundCompute || !Matches(
                request.References,
                computeAddress,
                foundCompute.ComputeProfileId))
            return Blocked(RedisDeclarationError.ComputeProfileUnavailable);
        var storage = await resolveStorageName(
            new NamedStorageProfileRequest(
                request.TenantId,
                ResourceType,
                storageAddress.Name,
                foundTarget.Id,
                spec.Storage.SizeGb,
                spec.Storage.Profile.Revision),
            ct);
        if (storage is not ResolvedStorageProfile foundStorage || !Matches(
                request.References,
                storageAddress,
                foundStorage.ProfileId))
            return Blocked(RedisDeclarationError.StorageProfileUnavailable);
        var computePolicy = await loadCompute(
            new ComputeProfilePlanningRequest(
                request.TenantId,
                ResourceType,
                foundCompute.ComputeProfileId,
                foundTarget.Id,
                spec.Placement.ComputeProfile.Revision),
            session,
            ct);
        if (computePolicy is not ComputeProfilePlanningSnapshot pinnedCompute)
            return Blocked(RedisDeclarationError.ComputeProfileUnavailable);
        var storagePolicy = await loadStorage(
            new StorageProfilePlanningRequest(
                request.TenantId,
                ResourceType,
                foundStorage.ProfileId,
                foundTarget.Id,
                spec.Storage.SizeGb,
                spec.Storage.Profile.Revision),
            session,
            ct);
        if (storagePolicy is not StorageProfilePlanningSnapshot pinnedStorage)
            return Blocked(RedisDeclarationError.StorageProfileUnavailable);

        return new GitPreviewReferencesSnapshot(
        [
            .. request.References.Where(item => item.Address != targetAddress && item.Address != computeAddress &&
                item.Address != storageAddress),
            new GitPreviewResolvedReference(
                targetAddress,
                foundTarget.Id,
                new DeclarationVersionToken(
                [
                    new DeclarationDocumentVersion(
                        DeploymentTargetVersionKeys.Revision(foundTarget.Id),
                        foundTarget.Revision),
                    new DeclarationDocumentVersion(
                        DeploymentTargetVersionKeys.WorkloadConfiguration(foundTarget.Id),
                        foundTarget.WorkloadConfiguration.Revision),
                ])),
            new GitPreviewResolvedReference(
                computeAddress,
                foundCompute.ComputeProfileId,
                new DeclarationVersionToken(
                [
                    new DeclarationDocumentVersion(
                        ComputeProfileVersionKeys.Policy(foundCompute.ComputeProfileId),
                        pinnedCompute.PolicyRevision),
                ])),
            new GitPreviewResolvedReference(
                storageAddress,
                foundStorage.ProfileId,
                new DeclarationVersionToken(
                [
                    new DeclarationDocumentVersion(
                        StorageProfileVersionKeys.Policy(foundStorage.ProfileId),
                        pinnedStorage.PolicyRevision),
                ])),
        ]);
    }

    private static bool Matches(
        IReadOnlyList<GitPreviewResolvedReference> references,
        ResourceAddress address,
        string id) => references.All(item => item.Address != address || item.ResourceId == id);

    private sealed record RedisPlanningReferences(
        string? TargetId,
        string? ComputeId,
        string? StorageId
    );
}
