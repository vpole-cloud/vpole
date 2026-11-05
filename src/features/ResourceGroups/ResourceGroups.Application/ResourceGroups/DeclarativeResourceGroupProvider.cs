using Pole.Features.ResourceGroups.Application.References;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.Search;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Workloads;
using System.Collections.Immutable;
using System.Text.Json;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

public sealed record ResourceGroupDeclaration
{
    public string Description { get; init; } = "";

    public string[] Tags { get; init; } = [];
}

internal sealed class DeclarativeResourceGroupProvider(
    IMongoCollection<ResourceGroup> groups,
    IMongoCollection<IsolationScope> scopes,
    IGitOpsMutations git,
    StoreOutboxWrites outbox,
    MongoBeginResourceGroupDeletion deletion,
    ResourceGroupLifecycleReferences lifecycleReferences,
    MongoRunTransaction transactions) : ITransactionalDeclarativeResourceProvider,
        IDeclarativeResourceLifecycleProvider, IDeclarativeAdoptionPreviewProvider,
        IDeclarativeDerivedIdentityProvider, IDeclarativeReferencePreparation, IDeclarativeResourceViewProvider
{
    public string Kind => "ResourceGroup";

    public ResourceTypeId ResourceType => ResourceGroupSearchProjection.SourceType.ResourceTypeId;

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        ResourceGroupDeclarationRejected rejected => rejected.Reason switch
        {
            ResourceGroupDeclarationError.NameTooLong => "NameTooLong",
            ResourceGroupDeclarationError.ExplicitAdoptionRequired => "ExplicitAdoptionRequired",
            ResourceGroupDeclarationError.ResourceDeleting => "ResourceDeleting",
            ResourceGroupDeclarationError.ExpectedRevisionConflict => "ExpectedRevisionConflict",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        },
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public int MaximumNameLength => ResourceGroupFactory.MaximumNameLength;

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<ResourceGroupDeclaration>(spec);
        DeclarationJson.Require(value.Description is { Length: <= ResourceGroupFactory.MaximumDescriptionLength }
            && value.Tags is not null &&
            value.Tags.All(tag => tag is { Length: > 0 and <= ResourceGroupFactory.MaximumTagLength }
                && !string.IsNullOrWhiteSpace(tag)), DeclarationFieldPaths.Spec);

        return DeclarationJson.Write(value with
        {
            Description = value.Description.Trim(),
            Tags = value.Tags.Select(tag => tag.Trim()).Distinct().Order(StringComparer.Ordinal).ToArray()
        });
    }

    public IReadOnlyList<DeclarationStep> Steps(
        ResourceAddress address,
        JsonElement spec) => [new(
                DeclarationPhase.Prepare,
                [])];

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await groups.Find(
            session,
            ResourceGroupFilters.ByTechnicalName(request.TenantId, request.Name)).FirstOrDefaultAsync(ct))?.Id;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) =>
        git.ExecuteAsync(operation, async (
                session,
                token) => (await ApplyDeclarationAsync(
                operation,
                session,
                token)) switch
            {
                ResourceGroupDeclarationApplied applied => new(applied.ResourceId, Changed: applied.Changed),
                ResourceGroupDeclarationRejected rejected => new(operation.ResourceId, rejected)
            }, ct);

    private async Task<ApplyResourceGroupDeclarationResult> ApplyDeclarationAsync(
        GitOperation operation, IClientSessionHandle session, CancellationToken token)
    {
        var context = operation.Context;
        var declaration = operation.Declaration;

        if (declaration.Address.Name.Length > MaximumNameLength)
            return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.NameTooLong);

        var spec = DeclarationJson.Read<ResourceGroupDeclaration>(declaration.Spec);
        var current = await groups.Find(
            session,
            ResourceGroupFilters.ByTechnicalName(
                context.TenantId,
                declaration.Address.Name)).FirstOrDefaultAsync(token);

        if (current is not null && current.Id != operation.ResourceId)
            return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ExplicitAdoptionRequired);

        if (current is null)
        {
            var defaultScopeId = await git.ResolveAcceptedAsync(new(context, operation.ResourceId,
                DefaultScopeAddress(declaration.Address)), session, token);
            if (defaultScopeId is null)
                return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ExpectedRevisionConflict);

            var created = ResourceGroupFactory.Create(context.TenantId, declaration.Address.Name, spec.Description,
                ResourceGroupFactory.DefaultCreateOptions with
                {
                    Id = operation.ResourceId,
                    DefaultIsolationScopeId = defaultScopeId,
                    Tags = spec.Tags.ToImmutableHashSet()
                });
            var scope = IsolationScopeFactory.CreateDefault(context.TenantId, created.Id,
                new IsolationScopeFactory.CreateOptions(created.DefaultIsolationScopeId, created.CreatedAtUtc));
            try
            {
                await groups.InsertOneAsync(session, created, cancellationToken: token);
                await scopes.InsertOneAsync(session, scope, cancellationToken: token);
            }
            catch (MongoWriteException exception) when (
                MongoDuplicateKey.IsForIdIndex(exception, groups.CollectionNamespace.CollectionName) ||
                MongoDuplicateKey.IsForIndex(exception, ResourceGroupsIndexes.TechnicalName,
                    groups.CollectionNamespace.CollectionName) ||
                MongoDuplicateKey.IsForIdIndex(exception, scopes.CollectionNamespace.CollectionName) ||
                MongoDuplicateKey.IsForIndex(exception, IsolationScopeIndexes.TenantName,
                    scopes.CollectionNamespace.CollectionName))
            {
                return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ExpectedRevisionConflict);
            }
            await outbox(session, [OutboxWrite.Create(created.ToCreateEvent())], token);

            return new ResourceGroupDeclarationApplied(created.Id, true);
        }

        if (current.Lifecycle is not ActiveResourceGroupLifecycleState)
            return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ResourceDeleting);

        var replacement = current.UpdateMetadata(spec.Description, spec.Tags.ToImmutableHashSet(), DateTime.UtcNow);

        if (replacement.Revision == current.Revision)
            return new ResourceGroupDeclarationApplied(current.Id, false);

        var result = await groups.ReplaceOneAsync(
            session,
            ResourceGroupFilters.AtRevisionAndRegistrationFence(current),
            replacement,
            cancellationToken: token);

        if (result.ModifiedCount != 1)
            return new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ExpectedRevisionConflict);

        await outbox(session, [OutboxWrite.Create(new ResourceGroupUpdatedEvent(
                        replacement.Id, replacement.Name, replacement.Description,
                        new ResourceGroupUpdatedEventChanges(
                            UpdateLog<string>.Of(current.Description, replacement.Description),
                            UpdateLog<ImmutableHashSet<string>>.Of(
                                current.Tags,
                                replacement.Tags)), replacement.Revision)
                    { EventCore = EventCore.Create(context.TenantId) })], token);

        return new ResourceGroupDeclarationApplied(current.Id, true);
    }

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

    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await groups.Find(session, ResourceGroupFilters.ByTenantAndId(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        current ??= await groups.Find(session,
            ResourceGroupFilters.ByTechnicalName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();
        if (request.Operation is { } accepted && current.Id == accepted.ResourceId &&
            current.Name != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());


        if (current.Lifecycle is DeletingResourceGroupLifecycleState)
            return new DeclarationReadBlocked(
                new ResourceGroupDeclarationRejected(ResourceGroupDeclarationError.ResourceDeleting));

        var projection = new DeclarationProjection(DeclarationJson.Write(new { }), NormalizeAndValidate(
            DeclarationJson.Write(new ResourceGroupDeclaration
            {
                Description = current.Description,
                Tags = current.Tags.ToArray()
            })));
        return new CurrentDeclaration(current.Id,
            new([new(ResourceGroupVersionKeys.Revision(current.Id), current.Revision)]),
            projection, DeclarationConfigurationState.Configured);
    }

    public Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) => Task.FromResult<ProjectDesiredDeclarationOutcome>(
            new DeclarationProjection(DeclarationJson.Write(new { }), NormalizeAndValidate(request.Declaration.Spec)));
    public async Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
        IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await groups.Find(session,
            ResourceGroupFilters.ByTenantAndId(request.Operation.Context.TenantId, request.Operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == ResourceGroupVersionKeys.Revision(request.Operation.ResourceId));
        if (current is null)
            return expected is null ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
        if (expected is null || expected.Revision != current.Revision)
            return new DeclarationPreconditionsChanged();

        var written = await groups.UpdateOneAsync(session,
            ResourceGroupFilters.AtRevisionAndRegistrationFence(current),
            Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1),
            cancellationToken: cancellationToken);
        return written.ModifiedCount == 1 ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
    }
}
