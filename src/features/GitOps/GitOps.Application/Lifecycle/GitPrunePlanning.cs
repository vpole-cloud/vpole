using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Application.Runs;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Lifecycle;

public sealed record GitPruneNotRequired;

public sealed record GitPruneAuthorized(
    GitWorkClaim Claim
);

public sealed record GitPruneAwaitingConfirmation(
    string PreviewId
);

public sealed record GitPruneBlocked(
    IDeclarationFailure Failure
);

public union PlanGitPruneOutcome(
    GitPruneNotRequired,
    GitPruneAuthorized,
    GitPruneAwaitingConfirmation,
    GitPruneBlocked,
    StaleGitExecution);

// Admission runs after complete acceptance and before the first resource mutation.
public sealed class GitPrunePlanning(
    GitRunStore runs,
    GitLifecyclePreviewBuilder builder,
    IMongoCollection<RepositorySource> sources,
    IMongoCollection<GitSynchronizationState> states,
    IMongoCollection<GitReconciliationRun> executions,
    IMongoCollection<ManagedDeclaration> inventory,
    IMongoCollection<GitOperationPreview> previews,
    MongoRunTransaction transactions
)
{
    public Task<PlanGitPruneOutcome> PlanAsync(GitWorkClaim claim, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<PlanGitPruneOutcome>(
            async (tx, token) =>
            {
                var run = await runs.TouchAsync(claim.Handle, tx.Session, token);
                if (run is null || run.Input is not GitInputAccepted accepted)
                    return tx.Abort(new StaleGitExecution());
                var source = await sources.Find(tx.Session, GitRepositoryFilters.SourceRevision(run.Stamp))
                    .SingleAsync(token);
                var approvalRequired = await runs.RequiresFreshHumanApprovalAsync(run, tx.Session, token);
                var pending = ManagedDeclarationFilters.PendingRemoval(source.TenantId, source.Id);
                var pendingKinds = await inventory.Distinct(tx.Session, item => item.Kind, pending).ToListAsync(token);
                if (pendingKinds.Count == 0 && !approvalRequired)
                    return tx.Commit(new GitPruneNotRequired());
                if (source.Prune == GitPrunePolicy.Retain && !approvalRequired)
                {
                    var created = await inventory
                        .Find(
                            tx.Session,
                            ManagedDeclarationFilters.CreatedResources(
                                source.TenantId,
                                source.Id,
                                accepted.Snapshot.Resources.Select(item => item.ResourceId).ToArray()))
                        .Project(item => new { item.Kind, item.ResourceId })
                        .ToListAsync(token);
                    var identities = created.Select(item => (item.Kind, item.ResourceId)).ToHashSet();
                    var ambiguous = accepted.Snapshot.Resources.Any(item => pendingKinds.Contains(item.Kind) &&
                        !identities.Contains((item.Kind, item.ResourceId)));
                    if (!ambiguous)
                        return tx.Commit(new GitPruneNotRequired());
                }

                var state = await states.Find(tx.Session, GitRepositoryFilters.ActiveState(run.Stamp))
                    .SingleAsync(token);
                var outcome = await builder.BuildReconciliationAsync(
                    new GitReconciliationPreviewRequest(source, state, accepted.Snapshot),
                    tx.Session,
                    token);
                var now = await new GitDatabaseClock(sources).NowAsync(run.Stamp.Source, tx.Session, token);
                if (outcome is GitPreviewRejected rejected)
                {
                    var blocked = run.Complete(claim.Handle.Identity, GitRunCompletion.Blocked, now) with
                    {
                        Diagnostics =
                        [
                            new GitRunDiagnostic(
                                DeclarationFailureDiagnostics.CommonCode(rejected.Failure) ?? "LifecyclePreviewBlocked",
                                null,
                                null,
                                null),
                        ],
                    };
                    await SaveAsync(blocked, state.CompleteRun(run.Stamp, GitRunCompletion.Blocked), tx.Session, token);
                    return tx.Commit(new GitPruneBlocked(rejected.Failure));
                }

                var preview = ((GitPreviewBuilt)outcome.Value).Preview;
                if (!approvalRequired && source.Prune == GitPrunePolicy.Prune && preview.CanAuthorizePrune())
                {
                    preview = preview.AuthorizePrune(
                        new GitPreviewPolicyAuthorizationContext(source.Revision, now, run.Id));
                    var authorized = run.AuthorizePrune(preview);
                    await previews.InsertOneAsync(tx.Session, preview, cancellationToken: token);
                    await executions.ReplaceOneAsync(
                        tx.Session,
                        GitRepositoryFilters.LiveClaim(run.Stamp, claim.Handle.Identity),
                        authorized,
                        cancellationToken: token);
                    return tx.Commit(new GitPruneAuthorized(claim with { Source = source, Run = authorized }));
                }

                await previews.InsertOneAsync(tx.Session, preview, cancellationToken: token);
                var waiting
                    = run.AwaitApproval(preview).Complete(claim.Handle.Identity, GitRunCompletion.Blocked, now) with
                    {
                        Diagnostics = [new GitRunDiagnostic("AwaitingConfirmation", null, null, null)],
                    };
                await SaveAsync(waiting, state.CompleteRun(run.Stamp, GitRunCompletion.Blocked), tx.Session, token);
                return tx.Commit(new GitPruneAwaitingConfirmation(preview.Id));
            },
            ct);

    private async Task SaveAsync(
        GitReconciliationRun run,
        GitSynchronizationState state,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        await executions.ReplaceOneAsync(
            session,
            GitRepositoryFilters.Run(run.Stamp.Source.TenantId, run.Stamp.Source.SourceId, run.Id),
            run,
            cancellationToken: ct);
        await states.ReplaceOneAsync(
            session,
            GitRepositoryFilters.ActiveState(run.Stamp),
            state,
            cancellationToken: ct);
    }
}
