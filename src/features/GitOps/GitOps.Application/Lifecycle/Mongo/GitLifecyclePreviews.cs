using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Application.Inventory.Mongo.Filters;
using Pole.Features.GitOps.Application.Lifecycle.Mongo.Filters;
using Pole.Features.GitOps.Application.Mongo.Filters;
using Pole.Features.GitOps.Application.Planning.Mongo.Filters;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Events;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.GitOps.Application.Lifecycle.Mongo;

public sealed record CreateLifecyclePreview(
    GitSourceIdentity Source,
    long ExpectedRevision,
    GitPreviewIntent Intent);

public sealed record ConfirmLifecyclePreview(
    GitSourceIdentity Source,
    string PreviewId,
    long ExpectedPreviewRevision,
    string ActorUserId);

public sealed record GitPreviewNotFound;

public sealed record GitPreviewStale;

public sealed record GitLifecycleScheduled(string RunId);

public union CreateLifecyclePreviewOutcome(
    GitOperationPreview,
    GitSourceNotFound,
    GitSourceRevisionConflict,
    GitSourceBusy,
    GitPreviewReadFailed,
    GitPreviewRejected);

public union ConfirmLifecyclePreviewOutcome(
    GitLifecycleScheduled,
    GitSourceNotFound,
    GitSourceBusy,
    GitPreviewNotFound,
    GitPreviewStale,
    GitPreviewReadFailed);

public sealed record GitPreviewPage(
    IReadOnlyList<GitOperationPreview> Items,
    int Page,
    int PageSize,
    bool HasMore);

public sealed class GitLifecyclePreviews(
    IMongoCollection<GitOperationPreview> previews,
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<GitReconciliationRun> runs,
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitAcceptedReference> references,
    GitLifecycleSnapshotReader snapshots,
    GitLifecyclePreviewBuilder builder,
    GitLifecycleResources resources,
    GitLifecyclePreviewReferences previewReferences,
    RepositoryProviderRegistry repositories,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    public async Task<CreateLifecyclePreviewOutcome> CreateAsync(CreateLifecyclePreview request, CancellationToken ct)
    {
        var source = await sources.Find(GitRepositoryFilters.Source(request.Source.TenantId, request.Source.SourceId))
            .FirstOrDefaultAsync(ct);
        if (source is null || source.Activity == GitSourceActivity.Deleted)
            return new GitSourceNotFound();
        if (source.Revision != request.ExpectedRevision)
            return new GitSourceRevisionConflict();
        if (!CanPreview(source, request.Intent))
            return new GitSourceBusy();
        if (request.Intent is GitPreviewResourceChanges { Removal: GitPreviewRemovalDisposition.Retain } &&
            source.Prune != GitPrunePolicy.Retain)
            return new GitPreviewRejected(new GitLifecycleRetainPolicyRequired());

        if (request.Intent is GitPreviewAdoption selected &&
            (selected.Resources.Select(item => (item.Kind,
                item.Name)).Distinct().Count() != selected.Resources.Length ||
             selected.Resources.Select(item => (item.Kind,
                 item.ResourceId)).Distinct().Count() != selected.Resources.Length))
            return new GitPreviewRejected(new DeclarationIdentityChanged());
        if (request.Intent is GitPreviewReplacement replacements &&
            replacements.Resources.Select(item => (item.DesiredKind, item.DesiredName)).Distinct().Count() !=
            replacements.Resources.Length)
            return new GitPreviewRejected(new DeclarationIdentityChanged());

        GitPreviewInput input;
        if (request.Intent is GitPreviewDetachSource)
            input = new GitPreviewInventoryInput();
        else
        {
            var location = request.Intent is GitPreviewRelocateSource relocation ?
                relocation.Destination : source.Repository;
            var selectedIds = request.Intent switch
            {
                GitPreviewAdoption adoption => adoption.Resources.ToDictionary(item => new ResourceAddress(item.Kind,
                    item.Name), item => item.ResourceId),
                GitPreviewReplacement replacement => replacement.Resources.ToDictionary(item =>
                    new ResourceAddress(item.DesiredKind, item.DesiredName), _ => ObjectId.GenerateNewId().ToString()),
                GitPreviewResourceChanges or GitPreviewRemoval or GitPreviewDetachSource or
                    GitPreviewRelocateSource => new Dictionary<ResourceAddress, string>()
            };
            var read = await snapshots.ReadAsync(source, location, selectedIds, ct);
            if (read is GitPreviewReadFailed failed)
                return failed;
            input = new GitPreviewRepositoryInput(location, ((GitLifecycleSnapshotRead)read.Value).Snapshot);
        }

        return await transactions.InvokeOutcomeAsync<CreateLifecyclePreviewOutcome>(async (tx, token) =>
        {
            var current = await sources.Find(tx.Session,
                GitRepositoryFilters.Source(request.Source.TenantId,
                    request.Source.SourceId)).FirstOrDefaultAsync(token);
            if (current is null || current.Activity == GitSourceActivity.Deleted)
                return tx.Abort(new GitSourceNotFound());
            if (current.Revision != request.ExpectedRevision)
                return tx.Abort(new GitSourceRevisionConflict());
            if (!CanPreview(current, request.Intent))
                return tx.Abort(new GitSourceBusy());
            var state = await states.Find(tx.Session, GitRepositoryFilters.State(current.TenantId, current.Id))
                .SingleAsync(token);
            var built = await builder.BuildAsync(current, state, request.Intent, input, tx.Session, token);
            if (built is GitPreviewRejected rejected)
                return tx.Abort(rejected);
            var preview = ((GitPreviewBuilt)built.Value).Preview;
            await previews.InsertOneAsync(tx.Session, preview, cancellationToken: token);
            return tx.Commit(preview);
        }, ct);
    }

    public async Task<ConfirmLifecyclePreviewOutcome> ConfirmAsync(ConfirmLifecyclePreview request,
        CancellationToken ct)
    {
        var visibleSource = await sources.Find(GitRepositoryFilters.Source(request.Source.TenantId,
            request.Source.SourceId)).FirstOrDefaultAsync(ct);
        if (visibleSource is null || visibleSource.Activity == GitSourceActivity.Deleted)
            return new GitSourceNotFound();
        var preview = await previews.Find(GitLifecyclePreviewFilters.Preview(request.Source, request.PreviewId))
            .FirstOrDefaultAsync(ct);
        if (preview is null)
            return new GitPreviewNotFound();
        if (preview.State is GitPreviewConfirmed confirmed &&
            request.ExpectedPreviewRevision == preview.Revision - 1)
            return new GitLifecycleScheduled(confirmed.RunId);
        if (preview.Revision != request.ExpectedPreviewRevision || preview.State is not GitPreviewAwaitingConfirmation)
            return new GitPreviewStale();

        var access = await CheckRepositoryAsync(preview.Source, ct);
        if (access is GitPreviewReadFailed failed)
            return failed;
        if (access is GitPreviewStale stale)
            return stale;
        var displayUrl = ((RepositoryAccessGranted)access.Value).DisplayUrl;
        return await transactions.InvokeOutcomeAsync<ConfirmLifecyclePreviewOutcome>(async (tx, token) =>
        {
            var currentPreview = await previews.Find(tx.Session,
                    GitLifecyclePreviewFilters.Preview(request.Source, request.PreviewId))
                .FirstOrDefaultAsync(token);
            if (currentPreview is null)
                return tx.Abort(new GitPreviewNotFound());
            if (currentPreview.State is GitPreviewConfirmed consumed &&
                request.ExpectedPreviewRevision == currentPreview.Revision - 1)
                return tx.Commit(new GitLifecycleScheduled(consumed.RunId));
            if (currentPreview.Revision != request.ExpectedPreviewRevision ||
                currentPreview.State is not GitPreviewAwaitingConfirmation)
                return tx.Abort(new GitPreviewStale());

            var source = await sources.Find(tx.Session,
                GitRepositoryFilters.Source(request.Source.TenantId,
                    request.Source.SourceId)).FirstOrDefaultAsync(token);
            if (source is null || source.Activity == GitSourceActivity.Deleted)
                return tx.Abort(new GitSourceNotFound());
            if (!CanPreview(source, currentPreview.Intent))
                return tx.Abort(new GitSourceBusy());
            var state = await states.Find(tx.Session, GitRepositoryFilters.State(source.TenantId, source.Id))
                .SingleAsync(token);
            if (state.CurrentRun is { } running && (await runs.Find(tx.Session,
                    GitRepositoryFilters.Run(source.TenantId, source.Id, running.RunId)).SingleAsync(token)).Input
                is GitInputLifecycle)
                return tx.Abort(new GitSourceBusy());
            if (source.Revision != currentPreview.Source.ConfigurationRevision ||
                state.InventoryRevision != currentPreview.Source.InventoryRevision ||
                !await ValidateReferencesAsync(currentPreview, tx.Session, token) ||
                !await ValidateDependenciesAsync(currentPreview, tx.Session, token))
                return tx.Abort(new GitPreviewStale());

            var observations = await ObserveAsync(currentPreview, tx.Session, token);
            if (observations is null)
                return tx.Abort(new GitPreviewStale());
            var runId = ObjectId.GenerateNewId().ToString();
            var now = await new GitDatabaseClock(sources).NowAsync(request.Source, tx.Session, token);
            var context = new GitPreviewConfirmationContext(currentPreview.Id, currentPreview.Revision,
                currentPreview.Source with
                {
                    ConfigurationRevision = source.Revision,
                    InventoryRevision = state.InventoryRevision
                }, observations.Value, request.ActorUserId, now, runId);
            if (currentPreview.Evaluate(context) != GitPreviewValidity.Current)
                return tx.Abort(new GitPreviewStale());
            var nextPreview = currentPreview.Confirm(context);
            var nextSource = currentPreview.Intent switch
            {
                GitPreviewDetachSource => source.Activity == GitSourceActivity.Detaching ?
                    source : source.BeginDetach(),
                GitPreviewRelocateSource relocation => source with
                {
                    Repository = relocation.Destination,
                    DisplayUrl = displayUrl,
                    Revision = checked(source.Revision + 1)
                },
                GitPreviewResourceChanges or GitPreviewAdoption or GitPreviewRemoval or
                    GitPreviewReplacement => source
            };
            if (state.CurrentRun is { } active)
                await runs.UpdateOneAsync(tx.Session, GitRepositoryFilters.Run(source.TenantId, source.Id,
                    active.RunId),
                    Builders<GitReconciliationRun>.Update.Set(item => item.State,
                        new GitRunFinished(GitRunCompletion.Superseded, now)), cancellationToken: token);
            var pending = new GitPendingRequest(new(GitRunTrigger.Manual, request.ActorUserId), null);
            var nextState = (state with
            {
                CurrentRun = null,
                CompletedGeneration = state.RequestedGeneration,
                Fence = checked(state.Fence + 1)
            }).RequestSync(pending).BeginRun(runId);
            var stamp = new GitRunStamp(request.Source, runId, nextSource.Revision,
                nextState.CurrentRun!.Generation, nextState.Fence);
            var run = GitReconciliationRunFactory.Create(stamp, nextSource.Repository, pending.Initiator, now,
                new GitInputLifecycle(GitLifecycleExecutionPlanFactory.Create(nextPreview)), null);
            if (run.Commit is { } commit)
                nextState = nextState.Observe(commit).Attempt(commit);
            await sources.ReplaceOneAsync(tx.Session, GitRepositoryFilters.Source(source.TenantId, source.Id),
                nextSource, cancellationToken: token);
            if (currentPreview.Intent is GitPreviewRelocateSource)
                await inventory.UpdateManyAsync(tx.Session,
                    ManagedDeclarationFilters.SourceInventory(source.TenantId, source.Id),
                    Builders<ManagedDeclaration>.Update.Set(item => item.SourceUrl, nextSource.DisplayUrl),
                    cancellationToken: token);
            await states.ReplaceOneAsync(tx.Session, GitRepositoryFilters.State(source.TenantId, source.Id), nextState,
                cancellationToken: token);
            await runs.InsertOneAsync(tx.Session, run, cancellationToken: token);
            var written = await previews.ReplaceOneAsync(tx.Session,
                GitLifecyclePreviewFilters.Revision(request.Source, request.PreviewId, request.ExpectedPreviewRevision),
                nextPreview, cancellationToken: token);
            if (written.ModifiedCount != 1)
                return tx.Abort(new GitPreviewStale());
            foreach (var reference in nextPreview.References)
            {
                var key = GitAcceptedReferenceFilters.Key(request.Source, reference.ConsumerResourceId,
                    reference.Target.Kind, reference.Target.Name);
                var existing = await references.Find(tx.Session, GitAcceptedReferenceFilters.Identity(key))
                    .FirstOrDefaultAsync(token);
                if (existing is not null && existing.Target != reference.Target)
                    return tx.Abort(new GitPreviewStale());
                if (existing is null)
                    await references.InsertOneAsync(tx.Session,
                        new(key, request.Source, reference.ConsumerResourceId, reference.Target),
                            cancellationToken: token);
            }
            await outbox(tx.Session, [OutboxWrite.Create(new GitSourceWorkRequested(source.Id,
                EventCore.Create(source.TenantId)))], token);
            return tx.Commit(new GitLifecycleScheduled(runId));
        }, ct);
    }

    public Task<GitOperationPreview?> LoadAsync(GitSourceIdentity source, string preview, CancellationToken ct) =>
        previews.Find(GitLifecyclePreviewFilters.Preview(source, preview)).FirstOrDefaultAsync(ct)!;

    public async Task<GitPreviewPage> ListAsync(GitSourceIdentity source, int page, int pageSize, CancellationToken ct)
    {
        var items = await previews.Find(GitLifecyclePreviewFilters.Source(source)).SortByDescending(item => item.Id)
            .Skip((page - 1) * pageSize).Limit(pageSize + 1).ToListAsync(ct);
        return new(items.Take(pageSize).ToArray(), page, pageSize, items.Count > pageSize);
    }

    private async Task<ImmutableArray<GitPreviewObservation>?> ObserveAsync(GitOperationPreview preview,
        IClientSessionHandle session, CancellationToken ct)
    {
        var result = ImmutableArray.CreateBuilder<GitPreviewObservation>();
        foreach (var operation in preview.Operations)
        {
            var read = await resources.ReadAsync(preview.Source.Source.TenantId, operation.Resource, session, ct);
            if (read is DeclarationReadBlocked)
                return null;
            var owner = await inventory.Find(session, ManagedDeclarationFilters.Resource(preview.Source.Source.TenantId,
                operation.Resource.Kind, operation.Resource.ResourceId)).FirstOrDefaultAsync(ct);
            result.Add(new(operation.Resource, await previewReferences.PreconditionsAsync(
                new(preview.Source, operation, preview.References), session, ct)));
        }
        return result.ToImmutable();
    }

    private Task<bool> ValidateReferencesAsync(GitOperationPreview preview, IClientSessionHandle session,
        CancellationToken ct) => previewReferences.ValidateAsync(preview.Source, preview.References, session, ct);

    private async Task<bool> ValidateDependenciesAsync(GitOperationPreview preview, IClientSessionHandle session,
        CancellationToken ct)
    {
        foreach (var target in preview.DependencySets.Select(item => item.Target).Distinct())
        {
            var current = await resources.DependencySetsAsync(preview.Source.Source.TenantId, target, session, ct);
            var expected = preview.DependencySets.Where(item => item.Target == target).ToArray();
            if (current.Count != expected.Length || current.Any(set => !expected.Any(old => old.Owner == set.Owner &&
                    old.Dependencies.Length == set.Dependencies.Length && old.Dependencies.All(dependency =>
                        set.Dependencies.Any(value => value.Dependent == dependency.Dependent &&
                            value.Versions.OrderBy(item => item.DocumentId, StringComparer.Ordinal).SequenceEqual(
                                dependency.Versions.OrderBy(item => item.DocumentId, StringComparer.Ordinal)))))))
                return false;
        }
        return true;
    }

    private async Task<CheckPreviewRepositoryOutcome> CheckRepositoryAsync(GitPreviewSourceStamp source,
        CancellationToken ct)
    {
        if (source.Input is GitPreviewInventoryInput)
            return new RepositoryAccessGranted("");
        var input = (GitPreviewRepositoryInput)source.Input;
        var provider = repositories.Find(input.Repository.Provider);
        if (provider is null)
            return new GitPreviewReadFailed("RepositoryProviderUnavailable");
        var reference = new RepositorySourceReference(source.Source.TenantId, input.Repository.ConnectionId,
            input.Repository.ExternalRepositoryId);
        var access = await provider.CheckAccessAsync(reference, ct);
        if (access is not RepositoryAccessGranted granted)
            return new GitPreviewReadFailed("RepositoryAccessUnavailable");
        var commit = await provider.ResolveCommitAsync(new(reference, input.Repository.Branch), ct);
        return commit switch
        {
            RepositoryCommitResolved resolved => resolved.Commit == input.Snapshot.Commit
                ? granted : new GitPreviewStale(),
            RepositoryAccessDenied => new GitPreviewReadFailed(nameof(RepositoryAccessDenied)),
            RepositoryNotFound => new GitPreviewReadFailed(nameof(RepositoryNotFound)),
            RepositoryRefNotFound => new GitPreviewReadFailed(nameof(RepositoryRefNotFound)),
            RepositoryTemporarilyUnavailable => new GitPreviewReadFailed(nameof(RepositoryTemporarilyUnavailable))
        };
    }

    private static bool CanPreview(RepositorySource source, GitPreviewIntent intent) =>
        source.Activity is GitSourceActivity.Active or GitSourceActivity.Paused ||
        source.Activity == GitSourceActivity.Detaching && intent is GitPreviewDetachSource;


}

public union CheckPreviewRepositoryOutcome(
    RepositoryAccessGranted,
    GitPreviewReadFailed,
    GitPreviewStale);
