// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;
using static Pole.Features.Functions.Application.Declarations.FunctionDeclarations;

namespace Pole.Features.Functions.Application.Declarations;

internal sealed class FunctionDeclarationReferences(
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    ResolveNamedDeploymentTarget resolveTargetName,
    ResolveNamedComputeProfile resolveComputeName,
    LoadIsolationScopePlanningSnapshot loadScope,
    ResolveDeploymentTargetPlanningSnapshot loadTarget,
    ResolveComputeProfilePlanningSnapshot loadCompute
)
{
    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken ct)
    {
        var spec = FunctionDeclarationJson.Read(operation.Declaration.Spec);
        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var pinned = await transactions.InvokeOutcomeAsync<FunctionPlanningReferences>(
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
                return tx.Commit(new FunctionPlanningReferences(target, compute));
            },
            ct);

        var targetId = pinned.TargetId;
        if (targetId is null)
        {
            var found = await resolveTargetName(
                new NamedDeploymentTargetRequest(
                    operation.Context.TenantId,
                    FunctionResourceDeployment.ResourceType,
                    targetAddress.Name),
                ct);
            if (found is not DeploymentTargetLookupDto target)
                return Blocked(FunctionDeclarationError.DeploymentTargetUnavailable);

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
                    FunctionResourceDeployment.ResourceType,
                    computeAddress.Name,
                    targetId,
                    spec.Placement.ComputeProfile.Revision),
                ct);
            if (found is not ResolvedComputeProfile compute)
                return Blocked(FunctionDeclarationError.ComputeProfileUnavailable);

            var result = await git.PinReferenceAsync(
                new PinGitReferenceRequest(
                    operation.Context,
                    operation.ResourceId,
                    new GitReferenceTarget(computeAddress, compute.ComputeProfileId)),
                ct);
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

        var scopeRead = await loadScope(
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
            return Blocked(FunctionDeclarationError.ResourceGroupUnavailable);
        }

        var targetAddress = new ResourceAddress("DeploymentTarget", spec.Placement.DeploymentTarget);
        var targetId = PreviewReference(request.References, targetAddress)?.ResourceId;
        if (targetId is null)
        {
            if (await resolveTargetName(
                    new NamedDeploymentTargetRequest(request.TenantId, ResourceType, targetAddress.Name),
                    cancellationToken) is not DeploymentTargetLookupDto found)
                return Blocked(FunctionDeclarationError.DeploymentTargetUnavailable);
            targetId = found.Id;
        }

        if (await loadTarget(
                new DeploymentTargetAdmissionRequest(
                    request.TenantId,
                    ResourceType,
                    scope.ResourceId,
                    targetId,
                    DeploymentTargetRequiredCapabilities.None,
                    FunctionResourceDeployment.GatewaySourceName),
                session,
                cancellationToken) is not DeploymentTargetPlanningSnapshot target)
            return Blocked(FunctionDeclarationError.DeploymentTargetUnavailable);

        var computeAddress = new ResourceAddress("ComputeProfile", spec.Placement.ComputeProfile.Name);
        var computeId = PreviewReference(request.References, computeAddress)?.ResourceId;
        if (computeId is null)
        {
            if (await resolveComputeName(
                    new NamedComputeProfileRequest(
                        request.TenantId,
                        ResourceType,
                        computeAddress.Name,
                        targetId,
                        spec.Placement.ComputeProfile.Revision),
                    cancellationToken) is not ResolvedComputeProfile found)
                return Blocked(FunctionDeclarationError.ComputeProfileUnavailable);
            computeId = found.ComputeProfileId;
        }

        if (await loadCompute(
                new ComputeProfilePlanningRequest(
                    request.TenantId,
                    ResourceType,
                    computeId,
                    targetId,
                    spec.Placement.ComputeProfile.Revision),
                session,
                cancellationToken) is not ComputeProfilePlanningSnapshot compute)
            return Blocked(FunctionDeclarationError.ComputeProfileUnavailable);

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

    private sealed record FunctionPlanningReferences(
        string? TargetId,
        string? ComputeId
    );
}
