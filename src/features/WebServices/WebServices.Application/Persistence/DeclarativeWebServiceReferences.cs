using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.WebServices.Application.Common;

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed partial class DeclarativeWebServiceProvider
{
    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(GitOperationInput operation,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var profileAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var pinned = await planningTransactions.InvokeOutcomeAsync<PlanningReferenceIds>(async (tx, ct) =>
        {
            var target = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, targetAddress),
                tx.Session, ct);
            var profile = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, profileAddress),
                tx.Session, ct);
            return tx.Commit(new PlanningReferenceIds(target, profile));
        }, cancellationToken);

        var targetId = pinned.TargetId;
        if (targetId is null)
        {
            var found = await resolveTargetName(new(operation.Context.TenantId,
                WebServiceResourceDeployment.ResourceType, targetAddress.Name), cancellationToken);
            if (found is not DeploymentTargetLookupDto target)
                return PlanningBlocked(found is NamedDeploymentTargetAmbiguous
                    ? WebServiceDeclarationError.DeploymentTargetNameAmbiguous
                    : WebServiceDeclarationError.DeploymentTargetUnavailable);

            var result = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
                new(targetAddress, target.Id)), cancellationToken);
            if (result is StaleGitExecution stale)
                return stale;
            targetId = ((GitReferencePinned)result.Value).ResourceId;
        }

        if (pinned.ProfileId is null)
        {
            var found = await resolveProfileName(new(operation.Context.TenantId,
                WebServiceResourceDeployment.ResourceType, profileAddress.Name, targetId,
                spec.Placement.ComputeProfile.Revision), cancellationToken);
            if (found is not ResolvedComputeProfile profile)
                return PlanningBlocked(found switch
                {
                    NamedComputeProfileAmbiguous => WebServiceDeclarationError.ComputeProfileNameAmbiguous,
                    ComputeProfileUnavailable { Reason: ComputeProfileUnavailableReason.RevisionUnavailable } =>
                        WebServiceDeclarationError.ComputeProfileRevisionUnavailable,
                    _ => WebServiceDeclarationError.PinnedComputeProfileUnavailable
                });

            var result = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
                new(profileAddress, profile.ComputeProfileId)), cancellationToken);
            if (result is StaleGitExecution stale)
                return stale;
        }

        return new DeclarationReferencesPrepared();
    }

    private sealed record PlanningReferenceIds(
        string? TargetId,
        string? ProfileId);
}
