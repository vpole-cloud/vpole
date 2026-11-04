using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed partial class DeclarativeWebServiceProvider
{
    public async Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<WebServiceDeclaration>(request.Consumer.Spec);
        var groupAddress = new ResourceAddress("ResourceGroup", spec.ResourceGroup);
        var group = PreviewReference(request.References, groupAddress);
        if (group is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(groupAddress));

        var scopeAddress = spec.Placement.IsolationScope is { } scopeName
            ? new ResourceAddress("IsolationScope", scopeName)
            : new ResourceAddress("DefaultIsolationScope", spec.ResourceGroup);
        var scope = PreviewReference(request.References, scopeAddress);
        if (spec.Placement.IsolationScope is not null && scope is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(scopeAddress));

        var scopeRead = await loadPlanningScope(new(request.TenantId, group.ResourceId, scope?.ResourceId),
            session, cancellationToken);
        if (scopeRead is IsolationScopePlanningSnapshot actualScope)
        {
            if (actualScope.IsDefault != (spec.Placement.IsolationScope is null) ||
                spec.Placement.IsolationScope is { } expectedName && actualScope.Name != expectedName)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(scopeAddress));
            scope = new(scopeAddress, actualScope.IsolationScopeId, new([
                new($"ResourceGroup/{actualScope.ResourceGroupId}", actualScope.ResourceGroupRevision),
                new($"IsolationScope/{actualScope.IsolationScopeId}", actualScope.ScopeRevision)]));
        }
        else if (scope is not { State: GitPreviewReferenceState.Planned })
            return PlanningBlocked(WebServiceDeclarationError.ResourceGroupUnavailable);

        var current = await services.Find(session,
            WebServiceFilters.ByTechnicalName(request.TenantId, request.Consumer.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var targetId = PreviewReference(request.References, targetAddress)?.ResourceId ??
            (current?.DeclaredPlacement?.DeploymentTargetName == targetAddress.Name
                ? current.DeclaredPlacement.DeploymentTargetId : null);
        if (targetId is null)
        {
            if (await resolveTargetName(new(request.TenantId, ResourceType, targetAddress.Name), cancellationToken)
                is not DeploymentTargetLookupDto found)
                return PlanningBlocked(WebServiceDeclarationError.DeploymentTargetUnavailable);
            targetId = found.Id;
        }

        if (await resolvePlanningTarget(new(request.TenantId, ResourceType, scope.ResourceId, targetId,
                RequiredCapabilities(spec)), session, cancellationToken) is not DeploymentTargetPlanningSnapshot target)
            return PlanningBlocked(WebServiceDeclarationError.DeploymentTargetUnavailable);

        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var computeId = PreviewReference(request.References, computeAddress)?.ResourceId ??
            (current?.DeclaredPlacement?.ComputeProfileName == computeAddress.Name
                ? current.DeclaredPlacement.ComputeProfileId : null);
        if (computeId is null)
        {
            if (await resolveProfileName(new(request.TenantId, ResourceType, computeAddress.Name, targetId,
                    spec.Placement.ComputeProfile.Revision), cancellationToken) is not ResolvedComputeProfile found)
                return PlanningBlocked(WebServiceDeclarationError.PinnedComputeProfileUnavailable);
            computeId = found.ComputeProfileId;
        }

        if (await resolvePlanningCompute(new(request.TenantId, ResourceType, computeId, targetId,
                spec.Placement.ComputeProfile.Revision), session, cancellationToken)
            is not ComputeProfilePlanningSnapshot compute)
            return PlanningBlocked(WebServiceDeclarationError.PinnedComputeProfileUnavailable);

        return new GitPreviewReferencesSnapshot([
            scope,
            new(targetAddress, targetId, new([
                new($"DeploymentTarget/{targetId}", target.Target.Revision),
                new($"DeploymentTarget/{targetId}/WorkloadConfiguration",
                    target.Target.WorkloadConfiguration.Revision)])),
            new(computeAddress, computeId, new([
                new($"ComputeProfile/{computeId}/Policy", compute.PolicyRevision),
                new($"ComputeProfile/{computeId}/Revision/{spec.Placement.ComputeProfile.Revision}", 0)]))]);
    }

    private static GitPreviewResolvedReference? PreviewReference(
        IReadOnlyList<GitPreviewResolvedReference> references, ResourceAddress address) =>
        references.SingleOrDefault(item => item.Address == address);
}
