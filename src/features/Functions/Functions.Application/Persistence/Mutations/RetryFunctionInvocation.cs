using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Domain;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Persistence.Mutations;

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

public delegate Task<Result<FunctionInvocationManualRetry, RetryFunctionInvocationError>>
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
    RunMongoOutcomeTransaction<Result<FunctionInvocationManualRetry, RetryFunctionInvocationError>>
        runTransaction)
{
    public Task<Result<FunctionInvocationManualRetry, RetryFunctionInvocationError>> InvokeAsync(
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

    private async Task<MongoTransactionDecision<Result<
        FunctionInvocationManualRetry,
        RetryFunctionInvocationError>>> RetryAsync(
        MongoTransactionContext<Result<FunctionInvocationManualRetry, RetryFunctionInvocationError>>
            transaction,
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
            return transaction.Abort(new Err<RetryFunctionInvocationError>(
                new FunctionAppNotFound(functionAppId)));
        }

        var invocation = await functionInvocations
            .Find(
                transaction.Session,
                FunctionInvocationRetryFilters.ByIdForFunctionApp(app, invocationId))
            .FirstOrDefaultAsync(cancellationToken);
        if (invocation is null)
        {
            return transaction.Abort(new Err<RetryFunctionInvocationError>(
                new FunctionInvocationNotFound(invocationId)));
        }
        if (invocation.State != FunctionInvocationState.Failed)
        {
            return transaction.Abort(new Err<RetryFunctionInvocationError>(
                new FunctionInvocationNotRetryable(invocationId)));
        }
        if (!string.Equals(app.ActiveRevisionId, invocation.RevisionId, StringComparison.Ordinal))
        {
            return transaction.Abort(new Err<RetryFunctionInvocationError>(
                new FunctionInvocationRevisionNoLongerActive(invocationId)));
        }

        var timer = await functionTimerStates
            .Find(
                transaction.Session,
                FunctionTimerFilters.ActiveForRetry(app, invocation))
            .FirstOrDefaultAsync(cancellationToken);
        if (timer is null || timer.RevisionId != invocation.RevisionId)
        {
            return transaction.Abort(new Err<RetryFunctionInvocationError>(
                new FunctionInvocationTimerNoLongerActive(invocationId)));
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
                return transaction.Abort(new Err<RetryFunctionInvocationError>(
                    new FunctionInvocationConcurrencyForbidden(invocationId)));
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
                return transaction.Abort(new Err<RetryFunctionInvocationError>(
                    new FunctionInvocationTimerChanged(invocationId)));
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
            return transaction.Abort(new Err<RetryFunctionInvocationError>(
                new FunctionInvocationAppChanged(app.Id)));
        }

        var retry = invocation.RetryManually(nowUtc);
        var replaced = await functionInvocations.ReplaceOneAsync(
            transaction.Session,
            FunctionInvocationRetryFilters.ForManualRetry(app, invocation),
            retry,
            cancellationToken: cancellationToken);
        if (replaced.ModifiedCount != 1)
        {
            return transaction.Abort(new Err<RetryFunctionInvocationError>(
                new FunctionInvocationChanged(invocationId)));
        }

        return transaction.Commit(new Ok<FunctionInvocationManualRetry>(
            new FunctionInvocationManualRetry(
                invocation.Id,
                checked(invocation.AttemptNumber + 1))));
    }
}
