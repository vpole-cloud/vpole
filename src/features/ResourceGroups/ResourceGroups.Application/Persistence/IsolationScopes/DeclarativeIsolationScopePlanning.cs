using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;

internal sealed partial class DeclarativeIsolationScopeProvider
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await scopes.Find(session, IsolationScopeFilters.ByTenantAndId(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        current ??= await scopes.Find(session, IsolationScopeFilters.ByTechnicalName(request.TenantId,
            request.Address.Name)).FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.Name != request.Address.Name || current.IsDefault)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        if (current.Lifecycle is not ActiveIsolationScopeLifecycleState)
            return new DeclarationReadBlocked(new IsolationScopeDeclarationUnavailable());

        return new CurrentDeclaration(current.Id,
            new([new($"IsolationScope/{current.Id}", current.Revision)]), Project(current.ResourceGroupId),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        ResourceGroupPlanningSnapshot? group;
        if (request.Operation is { } operation)
        {
            group = await AcceptedGroupAsync(operation, session, cancellationToken);
        }
        else
        {
            var spec = DeclarationJson.Read<IsolationScopeDeclaration>(request.Declaration.Spec);
            var id = await git.ResolveAsync(new(request.TenantId, new("ResourceGroup", spec.ResourceGroup)),
                session, cancellationToken);
            group = id is not null && await loadGroup(new(request.TenantId, id), session, cancellationToken)
                is ResourceGroupPlanningSnapshot found ? found : null;
        }

        return group is null
            ? new DeclarationReadBlocked(new IsolationScopeDeclarationUnavailable())
            : Project(group.ResourceGroupId) with { Dependencies = new(GroupVersions(group)) };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var group = await AcceptedGroupAsync(request.Operation, session, cancellationToken);
        if (group is null || !GroupVersions(group).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var current = await scopes.Find(session,
            IsolationScopeFilters.ByTenantAndId(request.Operation.Context.TenantId, request.Operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        var expected = request.Expected.Documents.SingleOrDefault(document =>
            document.DocumentId == $"IsolationScope/{request.Operation.ResourceId}");
        if (current is null ? expected is not null :
            expected?.Revision != current.Revision || current.ResourceGroupId != group.ResourceGroupId ||
            current.Lifecycle is not ActiveIsolationScopeLifecycleState)
            return new DeclarationPreconditionsChanged();

        // Scope lifecycle writes share the parent aggregate fence; no business timestamp/revision changes here.
        return await fenceGroup(group, session, cancellationToken) is ResourceGroupPlanningFenced
            ? new DeclarationFenced()
            : new DeclarationPreconditionsChanged();
    }
}
