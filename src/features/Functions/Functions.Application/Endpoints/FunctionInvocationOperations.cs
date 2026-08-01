using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Endpoints;

public static class FunctionInvocationOperations
{
    public static IEndpointRouteBuilder MapFunctionInvocationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/invocations", async (
                IMediator mediator,
                [AsParameters] GetFunctionInvocationsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetFunctionInvocations")
            .Produces<IReadOnlyList<FunctionInvocationDto>>();

        app.MapPost("/{Id}/invocations/{InvocationId}:retry", async (
                IMediator mediator,
                [AsParameters] RetryFunctionInvocationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(response => Results.Accepted(value: response));
            })
            .WithName("RetryFunctionInvocation")
            .Produces<RetryFunctionInvocationResponse>(StatusCodes.Status202Accepted);

        app.MapGet("/{Id}/usage", async (
                IMediator mediator,
                [AsParameters] GetFunctionUsageRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetFunctionUsage")
            .Produces<FunctionUsageResponse>();
        return app;
    }
}

public sealed record FunctionInvocationDto(
    string Id,
    string RevisionId,
    string TriggerId,
    string HandlerName,
    int ScheduleVersion,
    DateTimeOffset ScheduledForUtc,
    bool IsPastDue,
    int MissedOccurrenceCount,
    FunctionInvocationState State,
    int AttemptNumber,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? AttemptStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    FunctionInvocationError? Error);

public sealed record GetFunctionInvocationsRequest(
    [FromRoute] string Id,
    [FromQuery] int Take,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<FunctionInvocationDto>>, ITenantReadOperation;

public sealed class GetFunctionInvocationsHandler(IFunctionAppDbContext dbContext)
    : IMediatorHandler<GetFunctionInvocationsRequest, IReadOnlyList<FunctionInvocationDto>>
{
    public async Task<Result<IReadOnlyList<FunctionInvocationDto>>> HandleAsync(
        GetFunctionInvocationsRequest request,
        CancellationToken cancellationToken = default)
    {
        var app = await FunctionInvocationAppLookup.FindAppAsync(
            dbContext,
            request.TenantId,
            request.Id,
            cancellationToken);
        if (app is null)
            return Failures.NotFound("Function App not found.", request.Id);
        var take = request.Take == 0 ? 50 : Math.Clamp(request.Take, 1, 200);
        var invocations = await dbContext.FunctionInvocations.Find(candidate =>
                candidate.TenantId == app.TenantId && candidate.FunctionAppId == app.Id)
            .SortByDescending(candidate => candidate.CreatedAtUtc)
            .Limit(take)
            .ToListAsync(cancellationToken);
        return invocations.Select(ToDto).ToArray();
    }

    private static FunctionInvocationDto ToDto(FunctionInvocation invocation) =>
        new(
            invocation.Id,
            invocation.RevisionId,
            invocation.TriggerId,
            invocation.HandlerName,
            invocation.ScheduleVersion,
            invocation.ScheduledForUtc,
            invocation.IsPastDue,
            invocation.MissedOccurrenceCount,
            invocation.State,
            invocation.AttemptNumber,
            invocation.CreatedAtUtc,
            invocation.UpdatedAtUtc,
            invocation.AttemptStartedAtUtc,
            invocation.CompletedAtUtc,
            invocation.NextAttemptAtUtc,
            invocation.Error);
}

public sealed record RetryFunctionInvocationRequest(
    [FromRoute] string Id,
    [FromRoute] string InvocationId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<RetryFunctionInvocationResponse>, ITenantWriteOperation;

public sealed record RetryFunctionInvocationResponse(string InvocationId, int NextAttemptNumber);

public sealed class RetryFunctionInvocationHandler(
    IFunctionAppDbContext dbContext,
    TimeProvider timeProvider)
    : IMediatorHandler<RetryFunctionInvocationRequest, RetryFunctionInvocationResponse>
{
    public async Task<Result<RetryFunctionInvocationResponse>> HandleAsync(
        RetryFunctionInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        using var session = await dbContext.Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await session.WithTransactionAsync<Result<RetryFunctionInvocationResponse>>(
            async (transaction, transactionCancellationToken) =>
            {
                var app = await dbContext.FunctionApps.Find(
                        transaction,
                        candidate => candidate.TenantId == request.TenantId && candidate.Id == request.Id)
                    .FirstOrDefaultAsync(transactionCancellationToken);
                if (app is null)
                    return Failures.NotFound("Function App not found.", request.Id);

                var invocation = await dbContext.FunctionInvocations.Find(
                        transaction,
                        candidate => candidate.Id == request.InvocationId &&
                                     candidate.TenantId == app.TenantId &&
                                     candidate.FunctionAppId == app.Id)
                    .FirstOrDefaultAsync(transactionCancellationToken);
                if (invocation is null)
                    return Failures.NotFound("Function invocation not found.", request.InvocationId);
                if (invocation.State != FunctionInvocationState.Failed)
                    return Failures.Conflict("Only a failed Function invocation can be retried.");
                if (!string.Equals(app.ActiveRevisionId, invocation.RevisionId, StringComparison.Ordinal))
                    return Failures.Conflict("Only an invocation from the active Function revision can be retried.");

                var timer = await dbContext.FunctionTimerStates.Find(
                        transaction,
                        candidate => candidate.TenantId == app.TenantId &&
                                     candidate.FunctionAppId == app.Id &&
                                     candidate.TriggerId == invocation.TriggerId &&
                                     candidate.Enabled)
                    .FirstOrDefaultAsync(transactionCancellationToken);
                if (timer is null || timer.RevisionId != invocation.RevisionId)
                    return Failures.Conflict("The invocation timer is no longer active.");

                var now = timeProvider.GetUtcNow();
                if (timer.ConcurrencyPolicy == TimerConcurrencyPolicy.Forbid)
                {
                    var overlapping = await dbContext.FunctionInvocations.CountDocumentsAsync(
                        transaction,
                        candidate => candidate.Id != invocation.Id &&
                                     candidate.TenantId == app.TenantId &&
                                     candidate.FunctionAppId == app.Id &&
                                     candidate.TriggerId == invocation.TriggerId &&
                                     (candidate.State == FunctionInvocationState.Pending ||
                                      candidate.State == FunctionInvocationState.Dispatching ||
                                      candidate.State == FunctionInvocationState.Running ||
                                      candidate.State == FunctionInvocationState.RetryScheduled),
                        new CountOptions { Limit = 1 },
                        transactionCancellationToken);
                    if (overlapping != 0)
                        return Failures.Conflict("The timer concurrency policy forbids overlapping invocations.");

                    // Serialize this admission with the scheduler and with other manual retries.
                    var timerStamp = now > timer.UpdatedAtUtc ? now : timer.UpdatedAtUtc.AddTicks(1);
                    var timerUpdate = await dbContext.FunctionTimerStates.UpdateOneAsync(
                        transaction,
                        candidate => candidate.Id == timer.Id &&
                                     candidate.UpdatedAtUtc == timer.UpdatedAtUtc,
                        Builders<FunctionTimerState>.Update.Set(candidate => candidate.UpdatedAtUtc, timerStamp),
                        cancellationToken: transactionCancellationToken);
                    if (timerUpdate.ModifiedCount != 1)
                        return Failures.Conflict("The timer state changed concurrently.");
                }

                // Promotion and deletion update this document, making revision pinning a transaction barrier.
                var appStamp = now > app.UpdatedAtUtc ? now : app.UpdatedAtUtc.AddTicks(1);
                var appUpdate = await dbContext.FunctionApps.UpdateOneAsync(
                    transaction,
                    candidate => candidate.TenantId == app.TenantId &&
                                 candidate.Id == app.Id &&
                                 candidate.ActiveRevisionId == invocation.RevisionId &&
                                 candidate.UpdatedAtUtc == app.UpdatedAtUtc,
                    Builders<FunctionApp>.Update.Set(candidate => candidate.UpdatedAtUtc, appStamp),
                    cancellationToken: transactionCancellationToken);
                if (appUpdate.ModifiedCount != 1)
                    return Failures.Conflict("The Function App state changed concurrently.");

                var retry = invocation.RetryManually(now);
                var replaced = await dbContext.FunctionInvocations.ReplaceOneAsync(
                    transaction,
                    candidate => candidate.Id == invocation.Id &&
                                 candidate.TenantId == app.TenantId &&
                                 candidate.FunctionAppId == app.Id &&
                                 candidate.State == FunctionInvocationState.Failed &&
                                 candidate.UpdatedAtUtc == invocation.UpdatedAtUtc,
                    retry,
                    cancellationToken: transactionCancellationToken);
                if (replaced.ModifiedCount != 1)
                    return Failures.Conflict("Function invocation state changed concurrently.");
                return new RetryFunctionInvocationResponse(
                    invocation.Id,
                    checked(invocation.AttemptNumber + 1));
            },
            cancellationToken: cancellationToken);
    }
}

public sealed record FunctionUsageBucketDto(
    DateTimeOffset HourUtc,
    string RevisionId,
    string TriggerId,
    string HandlerName,
    FunctionMeterTriggerKind TriggerKind,
    long InvocationCount,
    long AttemptCount,
    long SucceededAttemptCount,
    long FailedAttemptCount,
    long SkippedCount,
    long ExecutionDurationMilliseconds);

public sealed record FunctionUsageResponse(
    DateTimeOffset? AggregatedThroughUtc,
    IReadOnlyList<FunctionUsageBucketDto> Buckets);

public sealed record GetFunctionUsageRequest(
    [FromRoute] string Id,
    [FromQuery] DateTimeOffset? FromUtc,
    [FromQuery] DateTimeOffset? ToUtc,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<FunctionUsageResponse>, ITenantReadOperation;

public sealed class GetFunctionUsageHandler(IFunctionAppDbContext dbContext)
    : IMediatorHandler<GetFunctionUsageRequest, FunctionUsageResponse>
{
    public async Task<Result<FunctionUsageResponse>> HandleAsync(
        GetFunctionUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        var app = await FunctionInvocationAppLookup.FindAppAsync(
            dbContext,
            request.TenantId,
            request.Id,
            cancellationToken);
        if (app is null)
            return Failures.NotFound("Function App not found.", request.Id);
        if (request.FromUtc is { Offset: var fromOffset } && fromOffset != TimeSpan.Zero ||
            request.ToUtc is { Offset: var toOffset } && toOffset != TimeSpan.Zero ||
            request.FromUtc is not null && request.ToUtc is not null && request.FromUtc > request.ToUtc)
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["range"] = ["Usage range must be an ordered UTC interval."]
            });

        var filter = Builders<FunctionUsageBucket>.Filter.And(
            Builders<FunctionUsageBucket>.Filter.Eq(bucket => bucket.TenantId, app.TenantId),
            Builders<FunctionUsageBucket>.Filter.Eq(bucket => bucket.FunctionAppId, app.Id));
        if (request.FromUtc is { } fromUtc)
            filter &= Builders<FunctionUsageBucket>.Filter.Gte(bucket => bucket.HourUtc, fromUtc);
        if (request.ToUtc is { } toUtc)
            filter &= Builders<FunctionUsageBucket>.Filter.Lte(bucket => bucket.HourUtc, toUtc);
        var buckets = await dbContext.FunctionUsageBuckets.Find(filter)
            .SortBy(bucket => bucket.HourUtc)
            .ToListAsync(cancellationToken);
        var aggregatedThrough = await dbContext.FunctionMeterEvents.Find(meter =>
                meter.TenantId == app.TenantId &&
                meter.FunctionAppId == app.Id &&
                meter.AggregatedAtUtc != null)
            .SortByDescending(meter => meter.AggregatedAtUtc)
            .Project(meter => meter.AggregatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return new FunctionUsageResponse(
            aggregatedThrough,
            buckets.Select(bucket => new FunctionUsageBucketDto(
                bucket.HourUtc,
                bucket.RevisionId,
                bucket.TriggerId,
                bucket.HandlerName,
                bucket.TriggerKind,
                bucket.InvocationCount,
                bucket.AttemptCount,
                bucket.SucceededAttemptCount,
                bucket.FailedAttemptCount,
                bucket.SkippedCount,
                bucket.ExecutionDurationMilliseconds)).ToArray());
    }
}

file static class FunctionInvocationAppLookup
{
    public static async Task<FunctionApp?> FindAppAsync(
        IFunctionAppDbContext dbContext,
        string tenantId,
        string appId,
        CancellationToken cancellationToken) =>
        await dbContext.FunctionApps.Find(Builders<FunctionApp>.Filter.And(
                Builders<FunctionApp>.Filter.ForTenant(tenantId),
                Builders<FunctionApp>.Filter.Eq(app => app.Id, appId)))
            .FirstOrDefaultAsync(cancellationToken);
}
