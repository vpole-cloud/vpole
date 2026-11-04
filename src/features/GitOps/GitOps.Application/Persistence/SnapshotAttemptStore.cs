using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Persistence;

public delegate Task<GitSourceExecution?> ClaimSnapshotAttempt(
    GitMutationContext context,
    bool onlyIfDue,
    CancellationToken cancellationToken);

public delegate Task<bool> RejectStoredSnapshotSchema(
    GitSourceExecution source,
    CancellationToken cancellationToken);

public sealed record FinalizedSnapshotAttempt(SnapshotProgress Progress);

public sealed record SupersededSnapshotAttempt;

public union FinalizeSnapshotAttemptResult(
    FinalizedSnapshotAttempt,
    SupersededSnapshotAttempt);

public delegate Task<FinalizeSnapshotAttemptResult> FinalizeSnapshotAttempt(
    GitSourceExecution source,
    SnapshotApplyOutcome outcome,
    CancellationToken cancellationToken);

internal sealed class SnapshotAttemptStore(
    IMongoCollection<GitSourceExecution> sources,
    MongoRunTransaction transactions,
    TimeProvider clock)
{
    public async Task<GitSourceExecution?> ClaimAsync(GitMutationContext context, bool onlyIfDue, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var claim = GitSourceExecutionFilters.SourceRun(context);

        if (onlyIfDue)
            claim &= GitSourceExecutionFilters.Due(now);
        // Retain the atomic attempt increment: a delayed worker cannot write under a newer attempt.
        return await sources.FindOneAndUpdateAsync(claim,
            Builders<GitSourceExecution>.Update.Inc(item => item.Attempt, 1)
                .Set(item => item.Status, new ApplyingSnapshot())
                .Set(item => item.NextAttemptAtUtc, SnapshotExecutionPolicy.NextAttemptAt(new ApplyingSnapshot(), now)),
            new FindOneAndUpdateOptions<GitSourceExecution> { ReturnDocument = ReturnDocument.After }, ct);
    }

    public async Task<bool> RejectSchemaAsync(GitSourceExecution source, CancellationToken ct)
    {
        var rejected = source.ToDomain().RejectStoredSchema();
        var result = await sources.UpdateOneAsync(GitSourceExecutionFilters.Run(source.ToContext()),
            Builders<GitSourceExecution>.Update
                .Set(item => item.Status, rejected.Progress)
                .Set(
                item => item.NextAttemptAtUtc,
                SnapshotExecutionPolicy.NextAttemptAt(
                    rejected.Progress,
                    clock.GetUtcNow().UtcDateTime)), cancellationToken: ct);

        return result.MatchedCount == 1;
    }

    public Task<FinalizeSnapshotAttemptResult> FinalizeAsync(
        GitSourceExecution source,
        SnapshotApplyOutcome outcome,
        CancellationToken ct)
    {
        var completed = source.ToDomain().Complete(outcome);

        return transactions.InvokeOutcomeAsync<FinalizeSnapshotAttemptResult>(async (tx, token) =>
            {
                var update = Builders<GitSourceExecution>.Update.Set(item => item.Status, completed.Progress)
                    .Set(
                    item => item.NextAttemptAtUtc,
                    SnapshotExecutionPolicy.NextAttemptAt(completed.Progress, clock.GetUtcNow().UtcDateTime))
                    .Set(item => item.AppliedCommit, completed.AppliedCommit);
                // The accepted commit and bindings are immutable within this run. Use the claimed
                // snapshot, while the persistence predicate still checks tenant/source/run/fence/attempt.
                var result = await sources.UpdateOneAsync(
                    tx.Session,
                    GitSourceExecutionFilters.Run(source.ToContext()),
                    update,
                    cancellationToken: token);

                return result.MatchedCount == 1
                ? tx.Commit(new FinalizeSnapshotAttemptResult(new FinalizedSnapshotAttempt(completed.Progress)))
                : tx.Abort(new SupersededSnapshotAttempt());
            }, ct);
    }
}
