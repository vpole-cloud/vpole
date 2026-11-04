using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.RedisServices.Application.Persistence;

internal sealed partial class DeclarativeRedisProvider
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
}
