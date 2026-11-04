using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed partial class DeclarativeSecretProvider
{
    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session, CancellationToken ct)
    {
        var current = await ReadCurrentAsync(new(request.TenantId, request.Declaration.Address), session, ct);
        if (current is DeclarationReadBlocked blocked)
            return blocked;
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var spec = DeclarationJson.Read<SecretDeclaration>(NormalizeAndValidate(request.Declaration.Spec));
        var groupAddress = new ResourceAddress("ResourceGroup", spec.ResourceGroup);
        var groupId = request.References.SingleOrDefault(item => item.Address == groupAddress)?.ResourceId;
        if (groupId is null)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(groupAddress));

        string? scopeId = null;
        if (spec.IsolationScope is not null)
        {
            var scopeAddress = new ResourceAddress("IsolationScope", spec.IsolationScope);
            scopeId = request.References.SingleOrDefault(item => item.Address == scopeAddress)?.ResourceId;
            if (scopeId is null)
                return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(scopeAddress));
        }

        if (await loadPlanningScope(new(request.TenantId, groupId, scopeId), session, ct)
            is not IsolationScopePlanningSnapshot scope)
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(groupAddress));

        var desired = Project(groupId, scope.IsolationScopeId, spec.Purpose, spec.Keys) with
        { Dependencies = new(ScopeVersions(scope)) };
        if (!JsonElement.DeepEquals(found.Projection.Identity, desired.Identity))
            return new DeclarationReadBlocked(new DeclarationReplacementRequired());

        return new GitLifecycleAdoptionPreview(found, desired, request.References);
    }
}
