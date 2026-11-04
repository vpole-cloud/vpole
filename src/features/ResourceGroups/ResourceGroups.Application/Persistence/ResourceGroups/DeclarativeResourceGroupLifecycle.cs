using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

internal sealed partial class DeclarativeResourceGroupProvider
{
    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await groups.Find(session,
            ResourceGroupFilters.ByTenantAndId(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (request.Resource.Address.Kind != Kind || current.Name != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        if (!await lifecycleReferences.HasKnownRegistrationsAsync(request.TenantId, current.Id,
                session, cancellationToken))
            return new DeclarationReadBlocked(new GitLifecycleUnsupported());

        return new GitLifecycleResourceSnapshot(request.Resource, ResourceGroupLifecycleVersions.Group(current),
            current.Lifecycle is ActiveResourceGroupLifecycleState ? GitLifecyclePresence.Active :
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

        // The dependency graph deletes each additional scope first; never cascade a user scope implicitly.
        var children = await scopes.Find(session, IsolationScopeFilters.ByTenantAndResourceGroup(
            request.Authority.TenantId, request.Resource.ResourceId)).ToListAsync(cancellationToken);
        if (children.Any(item => !item.IsDefault))
            return new DeclarationReadBlocked(new GitLifecycleDependencyRetained());

        var outcome = await deletion.ExecuteAsync(new(request.Authority.TenantId, request.Resource.ResourceId,
            request.Authority), session, cancellationToken);
        return outcome switch
        {
            ResourceGroupDeletionCompleted => new GitLifecycleResourceRemoved(),
            ResourceGroupDeletionRequested => new GitLifecycleRemovalRequested(),
            ResourceGroupDeletionError error => error switch
            {
                ResourceGroupNotFound => new GitLifecycleResourceRemoved(),
                ResourceGroupContainsResources or ResourceGroupDeploymentInProgress =>
                    new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
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

        return new GitLifecycleAdoptionPreview(found,
            new(DeclarationJson.Write(new { }), NormalizeAndValidate(request.Declaration.Spec)), request.References);
    }

    public async Task<IReadOnlyList<GitPreviewResolvedReference>> ReserveDerivedIdentitiesAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await groups.Find(session,
            ResourceGroupFilters.ByTenantAndId(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return [new(DefaultScopeAddress(request.Resource.Address),
                ResourceGroupFactory.DefaultCreateOptions.DefaultIsolationScopeId,
                State: GitPreviewReferenceState.Planned)];

        var scope = await scopes.Find(session,
            IsolationScopeFilters.ByTenantAndId(request.TenantId, current.DefaultIsolationScopeId))
            .FirstOrDefaultAsync(cancellationToken);
        if (scope is null || !scope.IsDefault || scope.ResourceGroupId != current.Id)
            throw new InvalidOperationException("An existing resource group requires its owned default scope.");

        return [new(DefaultScopeAddress(request.Resource.Address), scope.Id,
            ResourceGroupLifecycleVersions.Scope(scope, current))];
    }

    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken cancellationToken)
    {
        var address = DefaultScopeAddress(operation.Declaration.Address);
        var selected = await transactions.InvokeOutcomeAsync<string>(async (transaction, ct) =>
        {
            var accepted = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, address),
                transaction.Session, ct);
            if (accepted is not null)
                return transaction.Commit(accepted);

            var current = await groups.Find(transaction.Session,
                ResourceGroupFilters.ByTenantAndId(operation.Context.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(ct);
            return transaction.Commit(current?.DefaultIsolationScopeId ??
                ResourceGroupFactory.DefaultCreateOptions.DefaultIsolationScopeId);
        }, cancellationToken);
        var pinned = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId, new(address, selected)),
            cancellationToken);
        return pinned switch
        {
            GitReferencePinned => new DeclarationReferencesPrepared(),
            StaleGitExecution stale => stale
        };
    }

    private static ResourceAddress DefaultScopeAddress(ResourceAddress group) =>
        new("DefaultIsolationScope", group.Name);
}
