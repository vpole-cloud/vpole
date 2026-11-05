using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Application.FunctionApps;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Invocations;

public sealed record FunctionInvocationNotFound(string InvocationId);

public sealed record FunctionInvocationNotRetryable(string InvocationId);

public sealed record FunctionInvocationRevisionNoLongerActive(string InvocationId);

public sealed record FunctionInvocationTimerNoLongerActive(string InvocationId);

public sealed record FunctionInvocationConcurrencyForbidden(string InvocationId);

public sealed record FunctionInvocationTimerChanged(string InvocationId);

public sealed record FunctionInvocationAppChanged(string FunctionAppId);

public sealed record FunctionInvocationChanged(string InvocationId);

public union RetryFunctionInvocationError(
    FunctionAppNotFound,
    FunctionInvocationNotFound,
    FunctionInvocationNotRetryable,
    FunctionInvocationRevisionNoLongerActive,
    FunctionInvocationTimerNoLongerActive,
    FunctionInvocationConcurrencyForbidden,
    FunctionInvocationTimerChanged,
    FunctionInvocationAppChanged,
    FunctionInvocationChanged);

public sealed record FunctionInvocationManualRetry(
    string InvocationId,
    int NextAttemptNumber);

public union PersistRetriedFunctionInvocationOutcome(
    FunctionInvocationManualRetry,
    RetryFunctionInvocationError);

public delegate Task<PersistRetriedFunctionInvocationOutcome>
    RetryFunctionInvocation(
        string tenantId,
        string functionAppId,
        string invocationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

internal sealed class MongoRetryFunctionInvocation(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionInvocation> functionInvocations,
    IMongoCollection<FunctionTimerState> functionTimerStates,
    RunMongoOutcomeTransaction<PersistRetriedFunctionInvocationOutcome>
        runTransaction)
{
    public Task<PersistRetriedFunctionInvocationOutcome> InvokeAsync(
        string tenantId,
        string functionAppId,
        string invocationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        runTransaction(
            (transaction, ct) => RetryAsync(
                transaction,
                tenantId,
                functionAppId,
                invocationId,
                nowUtc,
                ct),
            cancellationToken);

    private async Task<MongoTransactionDecision<PersistRetriedFunctionInvocationOutcome>> RetryAsync(
        MongoTransactionContext<PersistRetriedFunctionInvocationOutcome> transaction,
        string tenantId,
        string functionAppId,
        string invocationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var app = await functionApps
            .Find(
                transaction.Session,
                FunctionAppFilters.ByTenantAndId(tenantId, functionAppId))
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
        {
            return Reject(transaction, new FunctionAppNotFound(functionAppId));
        }

        var invocation = await functionInvocations
            .Find(
                transaction.Session,
                FunctionInvocationRetryFilters.ByIdForFunctionApp(app, invocationId))
            .FirstOrDefaultAsync(cancellationToken);
        if (invocation is null)
        {
            return Reject(transaction, new FunctionInvocationNotFound(invocationId));
        }
        if (invocation.State != FunctionInvocationState.Failed)
        {
            return Reject(transaction, new FunctionInvocationNotRetryable(invocationId));
        }
        if (!string.Equals(app.ActiveRevisionId, invocation.RevisionId, StringComparison.Ordinal))
        {
            return Reject(transaction, new FunctionInvocationRevisionNoLongerActive(invocationId));
        }

        var timer = await functionTimerStates
            .Find(
                transaction.Session,
                FunctionTimerFilters.ActiveForRetry(app, invocation))
            .FirstOrDefaultAsync(cancellationToken);
        if (timer is null || timer.RevisionId != invocation.RevisionId)
        {
            return Reject(transaction, new FunctionInvocationTimerNoLongerActive(invocationId));
        }

        if (timer.ConcurrencyPolicy == TimerConcurrencyPolicy.Forbid)
        {
            var overlapping = await functionInvocations.CountDocumentsAsync(
                transaction.Session,
                FunctionInvocationRetryFilters.Overlapping(app, invocation),
                new CountOptions { Limit = 1 },
                cancellationToken);
            if (overlapping != 0)
            {
                return Reject(transaction, new FunctionInvocationConcurrencyForbidden(invocationId));
            }

            var timerStamp = nowUtc > timer.UpdatedAtUtc
                ? nowUtc
                : timer.UpdatedAtUtc.AddTicks(1);
            var timerUpdated = await functionTimerStates.UpdateOneAsync(
                transaction.Session,
                FunctionTimerFilters.ForRetryStamp(timer),
                Builders<FunctionTimerState>.Update.Set(
                    candidate => candidate.UpdatedAtUtc,
                    timerStamp),
                cancellationToken: cancellationToken);
            if (timerUpdated.ModifiedCount != 1)
            {
                return Reject(transaction, new FunctionInvocationTimerChanged(invocationId));
            }
        }

        var appStamp = nowUtc > app.UpdatedAtUtc
            ? nowUtc
            : app.UpdatedAtUtc.AddTicks(1);
        var appUpdated = await functionApps.UpdateOneAsync(
            transaction.Session,
            FunctionAppFilters.ForRetry(app, invocation.RevisionId),
            Builders<FunctionApp>.Update.Set(candidate => candidate.UpdatedAtUtc, appStamp),
            cancellationToken: cancellationToken);
        if (appUpdated.ModifiedCount != 1)
        {
            return Reject(transaction, new FunctionInvocationAppChanged(app.Id));
        }

        var retry = invocation.RetryManually(nowUtc);
        var replaced = await functionInvocations.ReplaceOneAsync(
            transaction.Session,
            FunctionInvocationRetryFilters.ForManualRetry(app, invocation),
            retry,
            cancellationToken: cancellationToken);
        if (replaced.ModifiedCount != 1)
        {
            return Reject(transaction, new FunctionInvocationChanged(invocationId));
        }

        PersistRetriedFunctionInvocationOutcome accepted = new FunctionInvocationManualRetry(
            invocation.Id,
            checked(invocation.AttemptNumber + 1));
        return transaction.Commit(accepted);
    }

    private static MongoTransactionDecision<PersistRetriedFunctionInvocationOutcome> Reject(
        MongoTransactionContext<PersistRetriedFunctionInvocationOutcome> transaction,
        RetryFunctionInvocationError error) =>
        transaction.Abort(new PersistRetriedFunctionInvocationOutcome(error));
}
