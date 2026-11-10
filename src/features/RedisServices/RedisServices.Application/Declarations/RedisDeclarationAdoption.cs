using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Contracts;
using static Pole.Features.RedisServices.Application.Declarations.RedisDeclarations;
using static Pole.Features.RedisServices.Application.Declarations.RedisPlanning;

namespace Pole.Features.RedisServices.Application.Declarations;

internal sealed class RedisDeclarationAdoption(
    RedisDeclarationPlanner planner,
    RedisDeclarationReferences referencePreparation,
    LoadIsolationScopePlanningSnapshot loadScope,
    ResolveDeploymentTargetPlanningSnapshot loadTarget,
    ResolveComputeProfilePlanningSnapshot loadCompute,
    ResolveStorageProfilePlanningSnapshot loadStorage
)
{
    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var references = await referencePreparation.ReadPreviewReferencesAsync(
            new ReadPreviewReferencesRequest(request.TenantId, request.Declaration, request.References),
            session,
            ct);
        if (references is DeclarationReadBlocked blocked)
            return blocked;
        var accepted = ((GitPreviewReferencesSnapshot)references.Value).References;
        var spec = DeclarationJson.Read<RedisDeclaration>(request.Declaration.Spec);

        string? Reference(string kind, string name) => accepted.SingleOrDefault(item =>
                item.Address == new ResourceAddress(kind, name))
            ?.ResourceId;

        var groupId = Reference("ResourceGroup", spec.ResourceGroup);
        var scopeId = spec.Placement.IsolationScope is { } scopeName ? Reference("IsolationScope", scopeName) : null;
        if (groupId is null || (spec.Placement.IsolationScope is not null && scopeId is null))
            return Blocked(RedisDeclarationError.IsolationScopeUnavailable);
        var scope = await loadScope(new IsolationScopePlanningRequest(request.TenantId, groupId, scopeId), session, ct);
        if (scope is not IsolationScopePlanningSnapshot scopeState)
            return Blocked(RedisDeclarationError.IsolationScopeUnavailable);
        var current = await planner.ReadCurrentAsync(
            new ReadCurrentDeclarationRequest(request.TenantId, request.Declaration.Address),
            session,
            ct);
        if (current is DeclarationReadBlocked unavailable)
            return unavailable;
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        var target = await loadTarget(
            new DeploymentTargetAdmissionRequest(
                request.TenantId,
                ResourceType,
                scopeState.IsolationScopeId,
                Reference("DeploymentTarget", spec.Placement.DeploymentTarget)!,
                DeploymentTargetRequiredCapabilities.None),
            session,
            ct);
        if (target is not DeploymentTargetPlanningSnapshot targetState)
            return Blocked(RedisDeclarationError.DeploymentTargetUnavailable);
        var compute = await loadCompute(
            new ComputeProfilePlanningRequest(
                request.TenantId,
                ResourceType,
                Reference("ComputeProfile", spec.Placement.ComputeProfile.Name)!,
                targetState.Target.Id,
                spec.Placement.ComputeProfile.Revision),
            session,
            ct);
        if (compute is not ComputeProfilePlanningSnapshot computeState)
            return Blocked(RedisDeclarationError.ComputeProfileUnavailable);
        var storage = await loadStorage(
            new StorageProfilePlanningRequest(
                request.TenantId,
                ResourceType,
                Reference("StorageProfile", spec.Storage.Profile.Name)!,
                targetState.Target.Id,
                spec.Storage.SizeGb,
                spec.Storage.Profile.Revision),
            session,
            ct);
        if (storage is not StorageProfilePlanningSnapshot storageState)
            return Blocked(RedisDeclarationError.StorageProfileUnavailable);
        var desired = Project(
            DeclarationPhase.Configure,
            new RedisManagedIdentity(
                scopeState.ResourceGroupId,
                scopeState.IsolationScopeId,
                targetState.Target.Id,
                spec.Storage.SizeGb,
                storageState.Storage.ProfileId,
                StorageConfiguration(storageState.Storage)),
            computeState.Compute.ToResourceDeploymentConfiguration(),
            spec.Tags);
        if (!JsonElement.DeepEquals(found.Projection.Identity, desired.Identity))
            return new DeclarationReadBlocked(new DeclarationReplacementRequired());
        return new GitLifecycleAdoptionPreview(
            found,
            desired with
            {
                Dependencies = new DeclarationVersionToken(
                [
                    new DeclarationDocumentVersion(
                        ResourceGroupVersionKeys.Revision(scopeState.ResourceGroupId),
                        scopeState.ResourceGroupRevision),
                    new DeclarationDocumentVersion(
                        ResourceGroupVersionKeys.IsolationScopeRevision(scopeState.IsolationScopeId),
                        scopeState.ScopeRevision),
                    .. accepted.SelectMany(item => item.Version?.Documents ?? []),
                ]),
            },
            accepted);
    }
}
