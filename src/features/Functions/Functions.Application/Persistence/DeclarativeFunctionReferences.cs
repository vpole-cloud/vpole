using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Functions.Application.Common;

namespace Pole.Features.Functions.Application.Persistence;

internal sealed partial class DeclarativeFunctionProvider
{
    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken ct)
    {
        var spec = FunctionDeclarationJson.Read(operation.Declaration.Spec);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var pinned = await transactions.InvokeOutcomeAsync<FunctionPlanningReferences>(async (tx, token) =>
        {
            var target = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, targetAddress),
                tx.Session, token);
            var compute = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, computeAddress),
                tx.Session, token);
            return tx.Commit(new FunctionPlanningReferences(target, compute));
        }, ct);

        var targetId = pinned.TargetId;
        if (targetId is null)
        {
            var found = await resolveTargetName(new(operation.Context.TenantId,
                FunctionResourceDeployment.ResourceType, targetAddress.Name), ct);
            if (found is not DeploymentTargetLookupDto target)
                return Blocked(FunctionDeclarationError.DeploymentTargetUnavailable);

            var result = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
                new(targetAddress, target.Id)), ct);
            if (result is StaleGitExecution stale)
                return stale;

            targetId = ((GitReferencePinned)result.Value).ResourceId;
        }

        if (pinned.ComputeId is null)
        {
            var found = await resolveComputeName(new(operation.Context.TenantId,
                FunctionResourceDeployment.ResourceType, computeAddress.Name, targetId,
                spec.Placement.ComputeProfile.Revision), ct);
            if (found is not ResolvedComputeProfile compute)
                return Blocked(FunctionDeclarationError.ComputeProfileUnavailable);

            var result = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
                new(computeAddress, compute.ComputeProfileId)), ct);
            if (result is StaleGitExecution stale)
                return stale;
        }

        return new DeclarationReferencesPrepared();
    }

    private sealed record FunctionPlanningReferences(
    string? TargetId,
    string? ComputeId);
}
