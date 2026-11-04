using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Application.Common;

namespace Pole.Features.Functions.Application.Persistence;

internal sealed partial class DeclarativeFunctionProvider
{
    public async Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = FunctionDeclarationJson.Read(request.Consumer.Spec);
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

        var scopeRead = await loadScope(new(request.TenantId, group.ResourceId, scope?.ResourceId),
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
            return Blocked(FunctionDeclarationError.ResourceGroupUnavailable);

        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var targetId = PreviewReference(request.References, targetAddress)?.ResourceId;
        if (targetId is null)
        {
            if (await resolveTargetName(new(request.TenantId, ResourceType, targetAddress.Name), cancellationToken)
                is not DeploymentTargetLookupDto found)
                return Blocked(FunctionDeclarationError.DeploymentTargetUnavailable);
            targetId = found.Id;
        }

        if (await loadTarget(new(request.TenantId, ResourceType, scope.ResourceId, targetId,
                DeploymentTargetRequiredCapabilities.None,
                FunctionResourceDeployment.GatewaySourceName), session, cancellationToken)
            is not DeploymentTargetPlanningSnapshot target)
            return Blocked(FunctionDeclarationError.DeploymentTargetUnavailable);

        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var computeId = PreviewReference(request.References, computeAddress)?.ResourceId;
        if (computeId is null)
        {
            if (await resolveComputeName(new(request.TenantId, ResourceType, computeAddress.Name, targetId,
                    spec.Placement.ComputeProfile.Revision), cancellationToken) is not ResolvedComputeProfile found)
                return Blocked(FunctionDeclarationError.ComputeProfileUnavailable);
            computeId = found.ComputeProfileId;
        }

        if (await loadCompute(new(request.TenantId, ResourceType, computeId, targetId,
                spec.Placement.ComputeProfile.Revision), session, cancellationToken)
            is not ComputeProfilePlanningSnapshot compute)
            return Blocked(FunctionDeclarationError.ComputeProfileUnavailable);

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
