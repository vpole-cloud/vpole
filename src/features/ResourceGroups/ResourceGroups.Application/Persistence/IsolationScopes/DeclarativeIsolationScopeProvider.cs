using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Workloads;
using System.Text.Json;

namespace Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;

public sealed record IsolationScopeDeclaration(string ResourceGroup);

public sealed record IsolationScopeDeclarationUnavailable : IDeclarationFailure;

internal sealed class DeclarativeIsolationScopeProvider(
    IMongoCollection<ResourceGroup> groups,
    IMongoCollection<IsolationScope> scopes,
    IGitOpsMutations git,
    LoadResourceGroupPlanningSnapshot loadGroup,
    FenceResourceGroupPlanningSnapshot fenceGroup,
    StoreOutboxWrites outbox,
    TimeProvider clock,
    MongoBeginIsolationScopeDeletion deletion) : ITransactionalDeclarativeResourceProvider,
        IDeclarativeResourceLifecycleProvider, IDeclarativeAdoptionPreviewProvider
{
    public string Kind => "IsolationScope";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        IsolationScopeDeclarationUnavailable => nameof(IsolationScopeDeclarationUnavailable),
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public DeclarationFacts Facts(JsonElement spec) =>
        new(new("ResourceGroup", DeclarationJson.Read<IsolationScopeDeclaration>(spec).ResourceGroup), [], [], [], []);

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<IsolationScopeDeclaration>(spec);
        DeclarationJson.Require(DeclarationNames.Valid(declaration.ResourceGroup), DeclarationFieldPaths.ResourceGroup);
        return DeclarationJson.Write(declaration);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        DeclarationJson.Require(ResourceTechnicalName.IsValid(address.Name) &&
            address.Name != IsolationScopeFactory.DefaultScopeName, DeclarationFieldPaths.MetadataName);
        return
        [
            new(DeclarationPhase.Prepare,
                [new(new("ResourceGroup", DeclarationJson.Read<IsolationScopeDeclaration>(spec).ResourceGroup),
                    DeclarationPhase.Prepare)])
        ];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        (await scopes.Find(session, IsolationScopeFilters.ByTechnicalName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(cancellationToken))?.Id;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken cancellationToken) =>
        git.ExecuteAsync(operation, async (session, ct) =>
        {
            var parent = await AcceptedGroupAsync(operation.Input, session, ct);
            if (parent is null)
                return new(operation.ResourceId, new IsolationScopeDeclarationUnavailable());

            var current = await scopes.Find(session,
                IsolationScopeFilters.ByTenantAndId(operation.Context.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(ct);
            if (current is not null)
                return new(operation.ResourceId, new DeclarationPreconditionsChanged());

            var created = IsolationScopeFactory.CreateAdditional(operation.Context.TenantId,
                parent.ResourceGroupId, operation.Declaration.Address.Name,
                new(operation.ResourceId, clock.GetUtcNow().UtcDateTime));
            var written = await groups.UpdateOneAsync(session,
                ResourceGroupFilters.ActiveAtRevision(parent.TenantId, parent.ResourceGroupId, parent.Revision),
                Builders<ResourceGroup>.Update.Inc(group => group.Revision, 1)
                    .Set(group => group.UpdatedAtUtc, created.CreatedAtUtc), cancellationToken: ct);
            if (written.ModifiedCount != 1)
                return new(operation.ResourceId, new DeclarationPreconditionsChanged());

            try
            {
                await scopes.InsertOneAsync(session, created, cancellationToken: ct);
            }
            catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(exception,
                IsolationScopeIndexes.TenantName, scopes.CollectionNamespace.CollectionName))
            {
                return new(operation.ResourceId, new DeclarationPreconditionsChanged());
            }

            await outbox(session, [OutboxWrite.Create(created.ToCreatedEvent())], ct);
            return new(operation.ResourceId, Changed: true);
        }, cancellationToken);

    private async Task<ResourceGroupPlanningSnapshot?> AcceptedGroupAsync(
        GitOperationInput operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<IsolationScopeDeclaration>(operation.Declaration.Spec);
        var groupId = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
            new("ResourceGroup", spec.ResourceGroup)), session, cancellationToken);
        return groupId is not null && await loadGroup(new(operation.Context.TenantId, groupId),
            session, cancellationToken) is ResourceGroupPlanningSnapshot group ? group : null;
    }

    private static DeclarationDocumentVersion[] GroupVersions(ResourceGroupPlanningSnapshot group) =>
    [
        new(DeclarationVersionKeys.ResourceGroup(group.ResourceGroupId), group.Revision),
        new(DeclarationVersionKeys.IsolationScope(group.DefaultIsolationScopeId), group.ScopeRevision)
    ];

    private static DeclarationProjection Project(string groupId) =>
        new(DeclarationJson.Write(new { resourceGroupId = groupId }), DeclarationJson.Write(new { }));

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
            new([
                new(DeclarationVersionKeys.IsolationScope(current.Id), current.Revision)]),
            Project(current.ResourceGroupId),
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
            document.DocumentId == DeclarationVersionKeys.IsolationScope(request.Operation.ResourceId));
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
