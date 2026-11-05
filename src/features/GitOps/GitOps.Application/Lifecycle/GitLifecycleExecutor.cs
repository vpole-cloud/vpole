using Pole.Features.GitOps.Application.Mongo;
using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Lifecycle;

internal sealed record GitLifecycleActionCompleted;

internal sealed record GitLifecycleCleanupPending;

internal sealed record GitLifecycleActionBlocked(IDeclarationFailure Failure);

internal union ExecuteLifecycleActionOutcome(
    GitLifecycleActionCompleted,
    GitLifecycleCleanupPending,
    GitLifecycleActionBlocked,
    GitPreviewStale,
    StaleGitExecution);

public sealed class GitLifecycleExecutor(
    GitRunStore runs,
    GitLifecycleResources resources,
    GitLifecyclePreviewReferences previewReferences,
    MongoGitLifecycleGuard guard,
    DeclarativeProviderRegistry providers,
    RepositoryProviderRegistry repositories,
    GitAcceptedSnapshotExecutor snapshots,
    ManifestCompiler compiler,
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<GitReconciliationRun> executions,
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitOperationReceipt> receipts,
    MongoRunTransaction transactions)
{
    public async Task<RepositoryExecutionReport> ExecuteAsync(GitWorkClaim claim, CancellationToken ct)
    {
        var plan = ((GitInputLifecycle)claim.Run.Input).Plan;
        var stored = await receipts.Find(GitRepositoryFilters.Receipts(claim.Source.TenantId,
            claim.Source.Id, claim.Run.Id)).ToListAsync(ct);
        var pending = plan.Operations.Where(item => item.Action != GitLifecycleAction.Apply)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var complete = new HashSet<string>(StringComparer.Ordinal);
        var waiting = false;
        while (pending.Count > 0)
        {
            var ready = pending.Values.Where(item => item.Dependencies.All(complete.Contains)).ToArray();
            if (ready.Length == 0)
                return waiting ? await WaitAsync(claim, ct) : await BlockAsync(claim, "LifecycleDependencyBlocked", ct);

            foreach (var operation in ready)
            {
                if (!Completed(stored, operation))
                {
                    var access = await CheckAccessAsync(claim, plan, ct);
                    if (access is LifecycleAccessRejected rejected)
                        return await BlockAsync(claim, rejected.Code, ct);
                    if (access is LifecycleAccessTransient)
                        return await runs.DelayAsync(claim.Handle,
                            new(nameof(RepositoryTemporarilyUnavailable), null, null, null), ct)
                            ? new(RepositoryExecutionDisposition.Handled, null, []) : Lost();
                }
                var result = await ProcessAsync(claim, plan, operation, ct);
                switch (result)
                {
                    case GitLifecycleActionCompleted:
                        complete.Add(operation.Id);
                        break;
                    case GitLifecycleCleanupPending:
                        waiting = true;
                        break;
                    case GitPreviewStale:
                        return await BlockAsync(claim, nameof(GitLifecycleApprovalStale), ct);
                    case StaleGitExecution:
                        return Lost();
                    case GitLifecycleActionBlocked blocked:
                        return await BlockAsync(claim,
                            DeclarationFailureDiagnostics.Code(blocked.Failure,
                                providers.Find(operation.Resource.Kind)!)!,
                            ct);
                }
                pending.Remove(operation.Id);
            }
        }

        if (waiting)
            return await WaitAsync(claim, ct);
        if (plan.Intent is GitPreviewDetachSource)
            return await FinishDetachAsync(claim, ct);
        if (claim.Run.AcceptedSnapshot is not { } snapshot)
            throw new InvalidOperationException("Repository lifecycle execution requires its reviewed snapshot.");
        return await snapshots.ExecuteAsync(claim, snapshot, ct);
    }

    private async Task<ExecuteLifecycleActionOutcome> ProcessAsync(GitWorkClaim claim, GitLifecycleExecutionPlan plan,
        GitPreviewOperation operation, CancellationToken ct)
    {
        try
        {
            return await transactions.InvokeOutcomeAsync<ExecuteLifecycleActionOutcome>(async (tx, token) =>
            {
                var authority = claim.ToLifecycleAuthority(plan.PreviewId, operation.Id);
                var context = await guard.ReadAsync(authority, tx.Session, token);
                if (context is null)
                    return tx.Abort(new StaleGitExecution());
                if (Completed(context.Receipts, operation))
                    return tx.Commit(new GitLifecycleActionCompleted());
                if (!await ValidateAsync(context, tx.Session, token))
                    return tx.Abort(new GitPreviewStale());
                var approved = context.Operations.Single(item => item.Id == operation.Id);
                if (!GitLifecycleRemovalProgress.CanBegin(approved.Resource, context.Plan.DependencySets,
                        context.Receipts))
                    return tx.Commit(new GitLifecycleCleanupPending());
                var current = await resources.ReadAsync(authority.TenantId, operation.Resource, tx.Session, token);
                if (current is DeclarationReadBlocked blocked)
                    return tx.Abort(new GitLifecycleActionBlocked(blocked.Failure));

                if (operation.Action == GitLifecycleAction.Retain)
                {
                    await inventory.UpdateOneAsync(tx.Session, ManagedDeclarationFilters.Resource(authority.TenantId,
                        operation.Resource.Kind, operation.Resource.ResourceId),
                        Builders<ManagedDeclaration>.Update.Set(item => item.PendingRemoval, true),
                        cancellationToken: token);
                    await StoreReceiptAsync(claim, approved, GitLifecycleStep.Retain, tx.Session, token);
                    return tx.Commit(new GitLifecycleActionCompleted());
                }
                if (operation.Action == GitLifecycleAction.Detach)
                {
                    await ReleaseAsync(claim, approved, tx.Session, token);
                    await StoreReceiptAsync(claim, approved, GitLifecycleStep.Detach, tx.Session, token);
                    return tx.Commit(new GitLifecycleActionCompleted());
                }
                if (operation.Action == GitLifecycleAction.Adopt)
                {
                    var outcome = await AdoptAsync(claim, approved, authority, tx.Session, token);
                    return outcome is GitLifecycleActionCompleted ? tx.Commit(outcome) : tx.Abort(outcome);
                }

                if (current is GitLifecycleResourceAbsent)
                {
                    var finalizationDependencies = context.Plan.DependencySets.Where(set =>
                        set.Target == approved.Resource)
                        .SelectMany(set => set.Dependencies)
                        .Where(dependency => dependency.Order == GitLifecycleDependencyOrder.AfterRemovalRequested);
                    if (finalizationDependencies.Any(dependency => !context.Operations.Any(action =>
                            action.Resource.ResourceId == dependency.Dependent.ResourceId &&
                            Completed(context.Receipts, action))))
                        return tx.Commit(new GitLifecycleCleanupPending());
                    await ReleaseAsync(claim, approved, tx.Session, token);
                    await StoreReceiptAsync(claim, approved, GitLifecycleStep.RemovalCompleted, tx.Session, token);
                    return tx.Commit(new GitLifecycleActionCompleted());
                }

                var hasStarted = HasReceipt(context.Receipts, approved.Id, GitLifecycleStep.BeginRemoval);
                if (hasStarted)
                    return tx.Commit(new GitLifecycleCleanupPending());
                if (providers.Find(operation.Resource.Kind) is not IDeclarativeResourceLifecycleProvider provider)
                    return tx.Abort(new GitLifecycleActionBlocked(new GitLifecycleUnsupported()));
                var expectedDependencies = await AdvanceDependenciesAsync(context, operation.Resource,
                    tx.Session, token);
                var fenced = await resources.FenceDependenciesAsync(authority.TenantId, operation.Resource,
                    expectedDependencies, tx.Session, token);
                if (fenced is not DeclarationFenced)
                    return tx.Abort(new GitPreviewStale());
                // A dependency fence may advance documents also owned by this resource. Read its current
                // token in the same transaction, after validating the complete original preconditions.
                current = await resources.ReadAsync(authority.TenantId, operation.Resource, tx.Session, token);
                if (current is not GitLifecycleResourceSnapshot snapshot)
                    return tx.Abort(new GitPreviewStale());
                var removed = await provider.RemoveAsync(new(authority,
                    GitLifecycleResources.Reference(operation.Resource),
                    snapshot.Version), tx.Session, token);
                if (removed is DeclarationPreconditionsChanged)
                    return tx.Abort(new GitPreviewStale());
                if (removed is StaleGitExecution)
                    return tx.Abort(new StaleGitExecution());
                if (removed is DeclarationReadBlocked rejected)
                    return tx.Abort(new GitLifecycleActionBlocked(rejected.Failure));
                if (removed is GitLifecycleResourceRemoved)
                {
                    await ReleaseAsync(claim, approved, tx.Session, token);
                    await StoreReceiptAsync(claim, approved, GitLifecycleStep.RemovalCompleted, tx.Session, token);
                    return tx.Commit(new GitLifecycleActionCompleted());
                }
                await StoreReceiptAsync(claim, approved, GitLifecycleStep.BeginRemoval, tx.Session, token);
                return tx.Commit(new GitLifecycleCleanupPending());
            }, ct);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIdIndex(exception,
                                                        inventory.CollectionNamespace.CollectionName) ||
                                                    MongoDuplicateKey.IsForIndex(exception,
                                                        GitOpsIndexes.ResourceIdentity,
                                                        inventory.CollectionNamespace.CollectionName))
        {
            return new GitPreviewStale();
        }
    }

    private async Task<ExecuteLifecycleActionOutcome> AdoptAsync(GitWorkClaim claim, GitPreviewOperation operation,
        GitLifecycleAuthority authority, IClientSessionHandle session, CancellationToken ct)
    {
        var snapshot = claim.Run.AcceptedSnapshot!;
        var accepted = snapshot.Resources.Single(item => item.Kind == operation.Resource.Kind &&
            item.Name == operation.Resource.Name && item.ResourceId == operation.Resource.ResourceId);
        var compiled = compiler.Restore(snapshot.ApiVersion,
            snapshot.Resources.Select(item => new AcceptedManifestDeclaration(item.Kind, item.Name, item.SpecJson))
                .ToArray());
        if (!compiled.Valid)
            return new GitLifecycleActionBlocked(new DeclarationPostconditionFailed());
        var declaration = compiled.Declarations.Single(item => item.Address.Kind == operation.Resource.Kind &&
            item.Address.Name == operation.Resource.Name);
        var phase = snapshot.Operations.First(item => item.ResourceId == operation.Resource.ResourceId).Phase;
        var input = new GitOperationInput(claim.ToContext(snapshot.Commit), declaration, phase,
            operation.Resource.ResourceId);
        var expected = new DeclarationVersionToken(operation.Expected!.Versions.Select(item =>
            new DeclarationDocumentVersion(item.DocumentId, item.Revision)).ToArray());
        AdoptLifecycleResourceOutcome result;
        switch (providers.Find(operation.Resource.Kind))
        {
            case IDeclarativeResourceAdoptionProvider adopter:
                result = await adopter.AdoptAsync(new(authority, input, expected), session, ct);
                break;
            case ITransactionalDeclarativeResourceProvider transactional:
                // Claiming ownership must conflict with a manual write that already observed no owner.
                result = await transactional.FenceAsync(new(input, expected), session, ct) switch
                {
                    DeclarationFenced => new GitLifecycleResourceAdopted(),
                    DeclarationPreconditionsChanged changed => changed,
                    DeclarationReadBlocked rejected => rejected
                };
                break;
            default:
                return new GitLifecycleActionBlocked(new GitLifecycleUnsupported());
        }

        if (result is DeclarationPreconditionsChanged)
            return new GitPreviewStale();
        if (result is StaleGitExecution stale)
            return stale;
        if (result is DeclarationReadBlocked blocked)
            return new GitLifecycleActionBlocked(blocked.Failure);

        var slot = await inventory.Find(session, ManagedDeclarationFilters.Address(authority.TenantId,
            new(operation.Resource.Kind, operation.Resource.Name))).FirstOrDefaultAsync(ct);
        if (slot is not null && (slot.SourceId != authority.SourceId ||
                slot.Materialization == GitResourceMaterialization.Created))
            return new GitLifecycleActionBlocked(new GitDeclarationOwnershipConflict());
        var owned = new ManagedDeclaration(ManagedDeclarationFilters.AddressId(authority.TenantId,
                new(operation.Resource.Kind, operation.Resource.Name)), authority.TenantId, operation.Resource.Kind,
            operation.Resource.Name, authority.SourceId, claim.Source.DisplayUrl, operation.Resource.ResourceId,
            accepted.Hash, null, null, false, authority.RunId, nameof(GitLifecycleStep.Adopt),
            checked((slot?.MutationFence ?? 0) + 1), GitResourceMaterialization.Created,
            GitManagedProjectionsFactory.Empty());
        await inventory.ReplaceOneAsync(session, ManagedDeclarationFilters.Address(authority.TenantId,
            new(operation.Resource.Kind, operation.Resource.Name)), owned, new ReplaceOptions { IsUpsert = true }, ct);
        await AdvanceInventoryAsync(claim, session, ct);
        await StoreReceiptAsync(claim, operation, GitLifecycleStep.Adopt, session, ct);
        return new GitLifecycleActionCompleted();
    }

    private async Task<bool> ValidateAsync(ApprovedLifecycleExecution context, IClientSessionHandle session,
        CancellationToken ct)
    {
        var changedReferences = AdvanceReferences(context);
        var policy = context.Plan.Authorization is GitPreviewPolicyAuthorized;
        if (!policy && !await previewReferences.ValidateAsync(context.Plan.Source, changedReferences, session, ct))
            return false;
        foreach (var operation in context.Operations)
        {
            if (policy && operation.Action == GitLifecycleAction.Apply)
                continue;
            if (operation.Action != GitLifecycleAction.Apply && Completed(context.Receipts, operation))
                continue;
            var read = await resources.ReadAsync(context.Run.Stamp.Source.TenantId, operation.Resource, session, ct);
            if (read is DeclarationReadBlocked)
                return false;
            var owner = await inventory.Find(session,
                ManagedDeclarationFilters.Resource(context.Run.Stamp.Source.TenantId,
                operation.Resource.Kind, operation.Resource.ResourceId)).FirstOrDefaultAsync(ct);
            var current = await previewReferences.PreconditionsAsync(new(context.Plan.Source, operation,
                changedReferences), session, ct);
            if (current is null && HasReceipt(context.Receipts, operation.Id, GitLifecycleStep.BeginRemoval))
                continue;
            if (!GitLifecycleResources.Same(operation.Expected, current))
                return false;
        }
        return true;
    }

    private async Task ReleaseAsync(GitWorkClaim claim, GitPreviewOperation operation,
        IClientSessionHandle session, CancellationToken ct)
    {
        var owner = await inventory.Find(session, ManagedDeclarationFilters.Resource(claim.Source.TenantId,
            operation.Resource.Kind, operation.Resource.ResourceId)).FirstOrDefaultAsync(ct);
        if (owner is not null && owner.SourceId != claim.Source.Id)
            throw new InvalidOperationException("A lifecycle action cannot release another source's ownership.");
        if (operation.Action == GitLifecycleAction.Replace)
        {
            var next = operation.Replacement!;
            var declaration = claim.Run.AcceptedSnapshot!.Resources.Single(item => item.Kind == next.Kind &&
                item.Name == next.Name && item.ResourceId == next.ResourceId);
            if (owner is not null)
                await inventory.DeleteOneAsync(session, ManagedDeclarationFilters.Resource(claim.Source.TenantId,
                    operation.Resource.Kind, operation.Resource.ResourceId), cancellationToken: ct);
            var reservation = new ManagedDeclaration(ManagedDeclarationFilters.AddressId(claim.Source.TenantId,
                    new(next.Kind, next.Name)), claim.Source.TenantId, next.Kind, next.Name, claim.Source.Id,
                claim.Source.DisplayUrl, next.ResourceId, declaration.Hash, null, null, false, claim.Run.Id,
                nameof(GitLifecycleStep.RemovalCompleted), 0, GitResourceMaterialization.Allocated,
                GitManagedProjectionsFactory.Empty());
            await inventory.InsertOneAsync(session, reservation, cancellationToken: ct);
            await AdvanceInventoryAsync(claim, session, ct);
        }
        else if (owner is not null)
        {
            await inventory.DeleteOneAsync(session, ManagedDeclarationFilters.Resource(claim.Source.TenantId,
                operation.Resource.Kind, operation.Resource.ResourceId), cancellationToken: ct);
            await AdvanceInventoryAsync(claim, session, ct);
        }
    }

    private Task AdvanceInventoryAsync(GitWorkClaim claim, IClientSessionHandle session, CancellationToken ct) =>
        states.UpdateOneAsync(session, GitRepositoryFilters.ActiveState(claim.Run.Stamp),
            Builders<GitSynchronizationState>.Update.Inc(item => item.InventoryRevision, 1), cancellationToken: ct);

    private async Task StoreReceiptAsync(GitWorkClaim claim, GitPreviewOperation operation, GitLifecycleStep step,
        IClientSessionHandle session, CancellationToken ct)
    {
        var run = await runs.TouchAsync(claim.Handle, session, ct)
                  ?? throw new InvalidOperationException("The lifecycle transaction lost its written execution fence.");
        var plan = ((GitInputLifecycle)run.Input).Plan;
        var referenceVersions = await previewReferences.RefreshVersionsAsync(
            new(claim.Source.TenantId, plan.References), session, ct);
        var related = new List<GitResourceVersion>();
        GitResourcePreconditions? postcondition = null;
        foreach (var action in plan.Operations)
        {
            var read = await resources.ReadAsync(claim.Source.TenantId, action.Resource, session, ct);
            if (read is not GitLifecycleResourceSnapshot current)
                continue;
            var observed = await previewReferences.PreconditionsAsync(new(plan.Source, action, referenceVersions),
                session, ct);
            related.AddRange(observed?.Versions ?? current.Version.Documents.Select(item =>
                new GitResourceVersion(item.DocumentId, item.Revision)).ToImmutableArray());
            if (action.Id == operation.Id)
            {
                var owner = await inventory.Find(session, ManagedDeclarationFilters.Resource(claim.Source.TenantId,
                    action.Resource.Kind, action.Resource.ResourceId)).FirstOrDefaultAsync(ct);
                postcondition = await previewReferences.PreconditionsAsync(new(plan.Source, action, referenceVersions),
                    session, ct) ?? GitLifecycleResources.Expected(action.Resource, owner?.SourceId, current);
            }
        }
        var state = await states.Find(session, GitRepositoryFilters.ActiveState(run.Stamp)).SingleAsync(ct);
        var hash = run.AcceptedSnapshot?.DesiredHash ?? DeclarationPlanValues.Hash(
            $"{plan.Source.Source.TenantId}/{plan.Source.Source.SourceId}/{plan.Source.InventoryRevision}");
        if (plan.Source.Input is GitPreviewRepositoryInput input)
            foreach (var consumer in input.Snapshot.Resources)
            {
                if (providers.Find(consumer.Kind) is not IDeclarativePreviewReferenceProvider)
                    continue;
                var refreshed = await previewReferences.ResolveAsync(claim.Source.TenantId, input.Snapshot, consumer,
                    referenceVersions, session, ct);
                if (refreshed is GitPreviewReferencesSnapshot known)
                    related.AddRange(known.References.Where(reference =>
                            providers.Find(reference.Address.Kind) is not IDeclarativeResourceLifecycleProvider)
                        .SelectMany(item => item.Version?.Documents ?? [])
                        .Select(item => new GitResourceVersion(item.DocumentId, item.Revision)));
            }
        foreach (var reference in referenceVersions.Where(item => item.State == GitPreviewReferenceState.Existing))
        {
            var refreshed = await resources.ReadAsync(claim.Source.TenantId, reference.Target, session, ct);
            if (refreshed is GitLifecycleResourceSnapshot current)
                related.AddRange(current.Version.Documents.Select(item =>
                    new GitResourceVersion(item.DocumentId, item.Revision)));
        }
        var receipt = new GitOperationReceipt(ReceiptId(run.Stamp, operation.Id, step), claim.Source.TenantId,
            claim.Source.Id, run.Id, run.Stamp.Fence, operation.Resource.Kind, operation.Resource.Name,
            ReceiptPhase(operation.Id, step), operation.Resource.ResourceId, hash, null,
            step != GitLifecycleStep.Retain, plan.PreviewRevision,
            new(run.MutationVersion, operation.Id, step, postcondition, state.InventoryRevision,
                GitLifecyclePreviewReferences.Merge(related), operation.Resource));
        await receipts.InsertOneAsync(session, receipt, cancellationToken: ct);
    }

    private static IReadOnlyList<GitPreviewPinnedReference> AdvanceReferences(ApprovedLifecycleExecution context)
    {
        var versions = context.Receipts.Where(item => item.Lifecycle is not null && item.Blocker is null)
            .Select(item => item.Lifecycle!).OrderBy(item => item.Sequence)
            .SelectMany(item => item.RelatedVersions.Concat(item.Postcondition?.Versions ?? []))
            .Concat(context.CleanupVersions)
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        return context.Plan.References.Select(reference => reference with
        {
            Versions = reference.Versions.Select(version => versions.GetValueOrDefault(version.DocumentId) ?? version)
                .ToImmutableArray()
        }).ToArray();
    }

    private async Task<IReadOnlyList<GitPreviewDependencySet>> AdvanceDependenciesAsync(
        ApprovedLifecycleExecution context, GitResourceIdentity target, IClientSessionHandle session,
        CancellationToken ct)
    {
        var observed = await resources.DependencySetsAsync(context.Run.Stamp.Source.TenantId, target, session, ct);
        var effects = context.Receipts.Where(item => item.Lifecycle is not null && item.Blocker is null)
            .Select(item => item.Lifecycle!).OrderBy(item => item.Sequence).ToArray();
        var removed = effects.Where(item => item.Step == GitLifecycleStep.RemovalCompleted)
            .Select(item => item.ActionId).ToHashSet(StringComparer.Ordinal);
        var versions = effects.SelectMany(item => item.RelatedVersions.Concat(item.Postcondition?.Versions ?? []))
            .Concat(context.CleanupVersions)
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        return context.Plan.DependencySets.Where(set => set.Target == target).Select(set => set with
        {
            Dependencies = set.Dependencies.Where(item => !context.Operations.Any(action =>
                    action.Resource.ResourceId == item.Dependent.ResourceId && removed.Contains(action.Id)) &&
                !(item.Order == GitLifecycleDependencyOrder.AfterDependentRemovalRequested &&
                    GitLifecycleRemovalProgress.HasStarted(item.Dependent, context.Receipts) &&
                    !observed.Any(current => current.Owner == set.Owner && current.Target == set.Target &&
                        current.Dependencies.Any(dependency => dependency.Dependent == item.Dependent &&
                            dependency.Order == item.Order))))
                .Select(item => item with
                {
                    Versions = item.Versions.Select(version =>
                    versions.GetValueOrDefault(version.DocumentId) ?? version).ToImmutableArray()
                }).ToImmutableArray()
        }).ToArray();
    }

    private async Task<RepositoryExecutionReport> FinishDetachAsync(GitWorkClaim claim, CancellationToken ct)
    {
        var finished = await transactions.InvokeOutcomeAsync<bool>(async (tx, token) =>
        {
            var run = await runs.TouchAsync(claim.Handle, tx.Session, token);
            if (run is null || run.Input is not GitInputLifecycle)
                return tx.Abort(false);
            var stored = await receipts.Find(tx.Session, GitRepositoryFilters.Receipts(claim.Source.TenantId,
                claim.Source.Id, run.Id)).ToListAsync(token);
            if (!((GitInputLifecycle)run.Input).Plan.Operations.All(operation =>
                    GitLifecycleReceiptCompletion.IsComplete(operation, stored)))
                return tx.Abort(false);
            var remaining = await inventory.CountDocumentsAsync(tx.Session,
                ManagedDeclarationFilters.SourceInventory(claim.Source.TenantId, claim.Source.Id),
                    cancellationToken: token);
            if (remaining != 0)
                return tx.Abort(false);
            var source = await sources.Find(tx.Session,
                GitRepositoryFilters.SourceRevision(run.Stamp)).SingleAsync(token);
            var state = await states.Find(tx.Session, GitRepositoryFilters.ActiveState(run.Stamp)).SingleAsync(token);
            var now = await new GitDatabaseClock(sources).NowAsync(run.Stamp.Source, tx.Session, token);
            var next = run.Complete(claim.Handle.Identity, GitRunCompletion.LifecycleCompleted, now);
            await executions.ReplaceOneAsync(tx.Session, GitRepositoryFilters.LiveClaim(run.Stamp,
                claim.Handle.Identity),
                next, cancellationToken: token);
            await states.ReplaceOneAsync(tx.Session, GitRepositoryFilters.ActiveState(run.Stamp),
                state.CompleteRun(run.Stamp, GitRunCompletion.LifecycleCompleted), cancellationToken: token);
            await sources.ReplaceOneAsync(tx.Session, GitRepositoryFilters.SourceRevision(run.Stamp),
                source.CompleteDetach(0), cancellationToken: token);
            return tx.Commit(true);
        }, ct);
        return finished ? new(RepositoryExecutionDisposition.Handled, null, []) : Lost();
    }

    private async Task<LifecycleAccessOutcome> CheckAccessAsync(GitWorkClaim claim, GitLifecycleExecutionPlan plan,
        CancellationToken ct)
    {
        if (plan.Source.Input is GitPreviewInventoryInput)
            return new LifecycleAccessGranted();
        var input = (GitPreviewRepositoryInput)plan.Source.Input;
        var provider = repositories.Find(input.Repository.Provider);
        if (provider is null)
            return new LifecycleAccessRejected("RepositoryProviderUnavailable");
        var reference = new RepositorySourceReference(claim.Source.TenantId, input.Repository.ConnectionId,
            input.Repository.ExternalRepositoryId);
        var access = await provider.CheckAccessAsync(reference, ct);
        if (access is RepositoryTemporarilyUnavailable)
            return new LifecycleAccessTransient();
        if (access is not RepositoryAccessGranted)
            return new LifecycleAccessRejected("RepositoryAccessUnavailable");
        if (plan.Authorization is GitPreviewPolicyAuthorized)
            return new LifecycleAccessGranted();
        var head = await provider.ResolveCommitAsync(new(reference, input.Repository.Branch), ct);
        if (head is RepositoryTemporarilyUnavailable)
            return new LifecycleAccessTransient();
        return head is RepositoryCommitResolved commit && commit.Commit == input.Snapshot.Commit
            ? new LifecycleAccessGranted() : new LifecycleAccessRejected(nameof(GitLifecycleApprovalStale));
    }

    private async Task<RepositoryExecutionReport> WaitAsync(GitWorkClaim claim, CancellationToken ct) =>
        await runs.WaitCleanupAsync(claim.Handle, ct)
            ? new(RepositoryExecutionDisposition.Handled, null, []) : Lost();

    private async Task<RepositoryExecutionReport> BlockAsync(GitWorkClaim claim, string code, CancellationToken ct) =>
        await runs.CompleteAsync(claim.Handle, GitRunCompletion.Blocked, [new(code, null, null, null)], ct)
            ? new(RepositoryExecutionDisposition.Handled, null, []) : Lost();

    internal static bool Completed(IReadOnlyList<GitOperationReceipt> stored, GitPreviewOperation operation) =>
        GitLifecycleReceiptCompletion.IsComplete(operation, stored);

    private static bool HasReceipt(IReadOnlyList<GitOperationReceipt> stored, string action, GitLifecycleStep step) =>
        stored.Any(item => item.Blocker is null && item.Lifecycle is { } effect && effect.ActionId == action &&
            effect.Step == step);

    private static string ReceiptPhase(string action, GitLifecycleStep step) => $"Lifecycle/{action}/{step}";

    private static string ReceiptId(GitRunStamp stamp, string action, GitLifecycleStep step) =>
        $"{stamp.Source.TenantId}/{stamp.Source.SourceId}/{stamp.RunId}/{ReceiptPhase(action, step)}";

    private static RepositoryExecutionReport Lost() => new(RepositoryExecutionDisposition.LeaseLost, null, []);
}

internal closed record LifecycleAccessOutcome;

internal sealed record LifecycleAccessGranted : LifecycleAccessOutcome;

internal sealed record LifecycleAccessRejected(string Code) : LifecycleAccessOutcome;

internal sealed record LifecycleAccessTransient : LifecycleAccessOutcome;
