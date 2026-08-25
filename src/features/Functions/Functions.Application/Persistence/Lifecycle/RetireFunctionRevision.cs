using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Lifecycle;

public delegate Task<FunctionRevisionLifecycleOutcome> RetireFunctionRevision(
    string tenantId,
    string functionAppId,
    string revisionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoRetireFunctionRevision(
    IMongoCollection<FunctionRevision> functionRevisions,
    IMongoCollection<FunctionInvocation> functionInvocations,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<FunctionRevisionLifecycleOutcome> runMongoTransaction)
{
    public async Task<FunctionRevisionLifecycleOutcome> InvokeAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        return await runMongoTransaction(async (transaction, transactionCancellationToken) =>
        {
            var revision = await functionRevisions.Find(
                    transaction.Session,
                    FunctionRevisionLifecycleFilters.RevisionByOwner(
                        tenantId,
                        functionAppId,
                        revisionId))
                .FirstOrDefaultAsync(transactionCancellationToken);
            if (revision is null)
            {
                return transaction.Abort(new FunctionRevisionNotFound(functionAppId, revisionId));
            }
            if (revision.Status is RetiredFunctionRevisionStatus)
                return transaction.Commit(new FunctionRevisionLifecycleCompleted());
            if (revision.Status is not DrainingFunctionRevisionStatus)
                return transaction.Abort(new FunctionRevisionNotDraining(revisionId));

            var durableWork = await functionInvocations.CountDocumentsAsync(
                transaction.Session,
                FunctionRevisionLifecycleFilters.DurableWork(
                    tenantId,
                    functionAppId,
                    revisionId),
                new CountOptions { Limit = 1 },
                transactionCancellationToken);
            if (durableWork != 0)
            {
                return transaction.Abort(new FunctionRevisionHasDurableInvocations(
                    functionAppId,
                    revisionId));
            }

            var retired = revision.Retire();
            var update = await functionRevisions.UpdateOneAsync(
                transaction.Session,
                FunctionRevisionLifecycleFilters.RevisionInStatus<DrainingFunctionRevisionStatus>(
                    tenantId,
                    functionAppId,
                    revisionId),
                Builders<FunctionRevision>.Update.Set(candidate => candidate.Status, retired.Status),
                cancellationToken: transactionCancellationToken);
            if (update.MatchedCount != 1)
            {
                return transaction.Abort(new FunctionRevisionConcurrencyConflict(
                    functionAppId,
                    revisionId));
            }

            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(revision.ToRetiredEvent())],
                transactionCancellationToken);
            return transaction.Commit(new FunctionRevisionLifecycleCompleted());
        }, cancellationToken);
    }
}
