using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;
using static Pole.Features.WebServices.Application.Declarations.WebServiceDeclarations;
using static Pole.Features.WebServices.Application.Declarations.WebServicePlanning;

namespace Pole.Features.WebServices.Application.Declarations;

internal sealed class WebServiceDeclarationReferences(
    IMongoCollection<WebService> services,
    IGitOpsMutations git,
    MongoRunTransaction planningTransactions,
    LoadIsolationScopePlanningSnapshot loadPlanningScope,
    ResolveComputeProfilePlanningSnapshot resolvePlanningCompute,
    ResolveDeploymentTargetPlanningSnapshot resolvePlanningTarget,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveProfileName
)
{
    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<WebServiceDeclaration>(operation.Declaration.Spec);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var profileAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var pinned = await planningTransactions.InvokeOutcomeAsync<PlanningReferenceIds>(
            async (tx, ct) =>
            {
                var target = await git.ResolveAcceptedAsync(
                    new ResolveAcceptedGitReferenceRequest(operation.Context, operation.ResourceId, targetAddress),
                    tx.Session,
                    ct);
                var profile = await git.ResolveAcceptedAsync(
                    new ResolveAcceptedGitReferenceRequest(operation.Context, operation.ResourceId, profileAddress),
                    tx.Session,
                    ct);
                return tx.Commit(new PlanningReferenceIds(target, profile));
            },
            cancellationToken);

        var targetId = pinned.TargetId;
        if (targetId is null)
        {
            var found = await resolveTargetName(
                new NamedDeploymentTargetRequest(
                    operation.Context.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    targetAddress.Name),
                cancellationToken);
            if (found is not DeploymentTargetLookupDto target)
                return PlanningBlocked(
                    found is NamedDeploymentTargetAmbiguous
                        ? WebServiceDeclarationError.DeploymentTargetNameAmbiguous
                        : WebServiceDeclarationError.DeploymentTargetUnavailable);

            var result = await git.PinReferenceAsync(
                new PinGitReferenceRequest(
                    operation.Context,
                    operation.ResourceId,
                    new GitReferenceTarget(targetAddress, target.Id)),
                cancellationToken);
            if (result is StaleGitExecution stale)
                return stale;
            targetId = ((GitReferencePinned)result.Value).ResourceId;
        }

        if (pinned.ProfileId is null)
        {
            var found = await resolveProfileName(
                new NamedComputeProfileRequest(
                    operation.Context.TenantId,
                    WebServiceResourceDeployment.ResourceType,
                    profileAddress.Name,
                    targetId,
                    spec.Placement.ComputeProfile.Revision),
                cancellationToken);
            if (found is not ResolvedComputeProfile profile)
                return PlanningBlocked(
                    found switch
                    {
                        NamedComputeProfileAmbiguous => WebServiceDeclarationError.ComputeProfileNameAmbiguous,
                        ComputeProfileUnavailable { Reason: ComputeProfileUnavailableReason.RevisionUnavailable } =>
                            WebServiceDeclarationError.ComputeProfileRevisionUnavailable,
                        _ => WebServiceDeclarationError.PinnedComputeProfileUnavailable,
                    });

            var result = await git.PinReferenceAsync(
                new PinGitReferenceRequest(
                    operation.Context,
                    operation.ResourceId,
                    new GitReferenceTarget(profileAddress, profile.ComputeProfileId)),
                cancellationToken);
            if (result is StaleGitExecution stale)
                return stale;
        }

        return new DeclarationReferencesPrepared();
    }

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

        var scopeRead = await loadPlanningScope(
            new IsolationScopePlanningRequest(request.TenantId, group.ResourceId, scope?.ResourceId),
            session,
            cancellationToken);
        if (scopeRead is IsolationScopePlanningSnapshot actualScope)
        {
            if (actualScope.IsDefault != spec.Placement.IsolationScope is null ||
                (spec.Placement.IsolationScope is { } expectedName && actualScope.Name != expectedName))
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(scopeAddress));
            scope = new GitPreviewResolvedReference(
                scopeAddress,
                actualScope.IsolationScopeId,
                new DeclarationVersionToken(
                [
                    new DeclarationDocumentVersion(
                        ResourceGroupVersionKeys.Revision(actualScope.ResourceGroupId),
                        actualScope.ResourceGroupRevision),
                    new DeclarationDocumentVersion(
                        ResourceGroupVersionKeys.IsolationScopeRevision(actualScope.IsolationScopeId),
                        actualScope.ScopeRevision),
                ]));
        }
        else if (scope is not { State: GitPreviewReferenceState.Planned })
        {
            return PlanningBlocked(WebServiceDeclarationError.ResourceGroupUnavailable);
        }

        var current = await services.Find(
                session,
                WebServiceFilters.ByTechnicalName(request.TenantId, request.Consumer.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var targetId = PreviewReference(request.References, targetAddress)?.ResourceId ??
            (current?.DeclaredPlacement?.DeploymentTargetName == targetAddress.Name
                ? current.DeclaredPlacement.DeploymentTargetId
                : null);
        if (targetId is null)
        {
            if (await resolveTargetName(
                    new NamedDeploymentTargetRequest(request.TenantId, ResourceType, targetAddress.Name),
                    cancellationToken) is not DeploymentTargetLookupDto found)
                return PlanningBlocked(WebServiceDeclarationError.DeploymentTargetUnavailable);
            targetId = found.Id;
        }

        if (await resolvePlanningTarget(
                new DeploymentTargetAdmissionRequest(
                    request.TenantId,
                    ResourceType,
                    scope.ResourceId,
                    targetId,
                    RequiredCapabilities(spec)),
                session,
                cancellationToken) is not DeploymentTargetPlanningSnapshot target)
            return PlanningBlocked(WebServiceDeclarationError.DeploymentTargetUnavailable);

        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var computeId = PreviewReference(request.References, computeAddress)?.ResourceId ??
            (current?.DeclaredPlacement?.ComputeProfileName == computeAddress.Name
                ? current.DeclaredPlacement.ComputeProfileId
                : null);
        if (computeId is null)
        {
            if (await resolveProfileName(
                    new NamedComputeProfileRequest(
                        request.TenantId,
                        ResourceType,
                        computeAddress.Name,
                        targetId,
                        spec.Placement.ComputeProfile.Revision),
                    cancellationToken) is not ResolvedComputeProfile found)
                return PlanningBlocked(WebServiceDeclarationError.PinnedComputeProfileUnavailable);
            computeId = found.ComputeProfileId;
        }

        if (await resolvePlanningCompute(
                new ComputeProfilePlanningRequest(
                    request.TenantId,
                    ResourceType,
                    computeId,
                    targetId,
                    spec.Placement.ComputeProfile.Revision),
                session,
                cancellationToken) is not ComputeProfilePlanningSnapshot compute)
            return PlanningBlocked(WebServiceDeclarationError.PinnedComputeProfileUnavailable);

        return new GitPreviewReferencesSnapshot(
        [
            scope,
            new GitPreviewResolvedReference(
                targetAddress,
                targetId,
                new DeclarationVersionToken(
                [
                    new DeclarationDocumentVersion(
                        DeploymentTargetVersionKeys.Revision(targetId),
                        target.Target.Revision),
                    new DeclarationDocumentVersion(
                        DeploymentTargetVersionKeys.WorkloadConfiguration(targetId),
                        target.Target.WorkloadConfiguration.Revision),
                ])),
            new GitPreviewResolvedReference(
                computeAddress,
                computeId,
                new DeclarationVersionToken(
                [
                    new DeclarationDocumentVersion(ComputeProfileVersionKeys.Policy(computeId), compute.PolicyRevision),
                    new DeclarationDocumentVersion(
                        ComputeProfileVersionKeys.Revision(computeId, spec.Placement.ComputeProfile.Revision),
                        0),
                ])),
        ]);
    }

    private sealed record PlanningReferenceIds(
        string? TargetId,
        string? ProfileId
    );
}
