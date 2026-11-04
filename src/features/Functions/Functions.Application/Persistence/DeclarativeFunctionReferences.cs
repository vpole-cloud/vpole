using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Application.Common;
using Pole.Database;
using static Pole.Features.Functions.Application.Persistence.FunctionDeclarations;

namespace Pole.Features.Functions.Application.Persistence;

internal sealed class FunctionDeclarationReferences(
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveComputeName,
    LoadIsolationScopePlanningSnapshot loadScope,
    ResolveDeploymentTargetPlanningSnapshot loadTarget,
    ResolveComputeProfilePlanningSnapshot loadCompute)
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
                new(
                    DeclarationVersionKeys.ResourceGroup(actualScope.ResourceGroupId),
                    actualScope.ResourceGroupRevision),
                new(DeclarationVersionKeys.IsolationScope(actualScope.IsolationScopeId), actualScope.ScopeRevision)]));
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
                new(DeclarationVersionKeys.DeploymentTarget(targetId), target.Target.Revision),
                new(DeclarationVersionKeys.TargetWorkloadConfiguration(targetId),
                    target.Target.WorkloadConfiguration.Revision)])),
            new(computeAddress, computeId, new([
                new(DeclarationVersionKeys.ComputeProfilePolicy(computeId), compute.PolicyRevision),
                new(
                    DeclarationVersionKeys.ComputeProfileRevision(computeId, spec.Placement.ComputeProfile.Revision),
                    0)]))]);
    }

}
