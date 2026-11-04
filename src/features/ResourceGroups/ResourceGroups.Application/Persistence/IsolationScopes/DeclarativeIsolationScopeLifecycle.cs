using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;

internal sealed partial class DeclarativeIsolationScopeProvider
{
    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await scopes.Find(session,
            IsolationScopeFilters.ByTenantAndId(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (current.IsDefault)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());
        if (request.Resource.Address.Kind != Kind || current.Name != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var parent = await groups.Find(session,
            ResourceGroupFilters.ByTenantAndId(request.TenantId, current.ResourceGroupId))
            .FirstOrDefaultAsync(cancellationToken);
        if (parent is null)
            return new DeclarationReadBlocked(new IsolationScopeDeclarationUnavailable());

        return new GitLifecycleResourceSnapshot(request.Resource, ResourceGroupLifecycleVersions.Scope(current, parent),
            current.Lifecycle is ActiveIsolationScopeLifecycleState ? GitLifecyclePresence.Active :
                GitLifecyclePresence.Deleting, GitLifecycleProtection.ConfigurationOnly,
                GitLifecycleVersionSemantics.ObservedDocuments);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var read = await ReadLifecycleAsync(new(request.Authority.TenantId, request.Resource),
            session, cancellationToken);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();

        if (read is DeclarationReadBlocked blocked)
            return blocked;
        if (!ResourceGroupLifecycleVersions.Same(((GitLifecycleResourceSnapshot)read.Value).Version, request.Expected))
            return new DeclarationPreconditionsChanged();

        var current = await scopes.Find(session, IsolationScopeFilters.ByTenantAndId(
            request.Authority.TenantId, request.Resource.ResourceId)).SingleAsync(cancellationToken);
        var outcome = await deletion.ExecuteAsync(new(request.Authority.TenantId, current.ResourceGroupId, current.Id,
            request.Authority), session, cancellationToken);
        return outcome switch
        {
            IsolationScopeDeletionCompleted => new GitLifecycleResourceRemoved(),
            IsolationScopeDeletionRequested => new GitLifecycleRemovalRequested(),
            IsolationScopeDeletionError error => error switch
            {
                IsolationScopeNotFound => new GitLifecycleResourceRemoved(),
                DefaultIsolationScopeDeletionForbidden =>
                    new DeclarationReadBlocked(new GitLifecycleResourceProtected()),
                ResourceGroupNotFound or ResourceGroupDeleting or IsolationScopeDeploymentInProgress or
                    IsolationScopeReferenced => new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
                ResourceGroupRevisionConflict conflict => conflict.Reason ==
                    ResourceGroupRevisionConflictReason.GitOwnedConfiguration
                    ? new StaleGitExecution() : new DeclarationPreconditionsChanged()
            }
        };
    }

    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(new(request.TenantId, request.Declaration.Address), session,
            cancellationToken);
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var spec = DeclarationJson.Read<IsolationScopeDeclaration>(request.Declaration.Spec);
        var reference = request.References.SingleOrDefault(item => item.Address == new ResourceAddress(
            "ResourceGroup", spec.ResourceGroup));
        if (reference is null || await loadGroup(new(request.TenantId, reference.ResourceId),
                session, cancellationToken) is not ResourceGroupPlanningSnapshot parent)
            return new DeclarationReadBlocked(new IsolationScopeDeclarationUnavailable());

        return new GitLifecycleAdoptionPreview(found, Project(parent.ResourceGroupId) with
        { Dependencies = new(GroupVersions(parent)) }, request.References);
    }
}
