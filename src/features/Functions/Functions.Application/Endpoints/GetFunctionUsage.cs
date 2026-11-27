// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Usage;
using Pole.Features.Functions.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Functions.Application.Common.FunctionAppHttpMappings;

namespace Pole.Features.Functions.Application.Endpoints;

public static class GetFunctionUsage
{
    public static IEndpointRouteBuilder MapGetFunctionUsageEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{Id}/usage",
                async (
                    IMediator mediator, [AsParameters] GetFunctionUsageRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        FunctionUsageResponse usage => Results.Ok(usage),
                        FunctionAppNotFound notFound => MapNotFound(notFound),
                    });
                })
            .WithName("GetFunctionUsage")
            .Produces<FunctionUsageResponse>();
        return app;
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
    long ExecutionDurationMilliseconds
);

public sealed record FunctionUsageResponse(
    DateTimeOffset? AggregatedThroughUtc,
    IReadOnlyList<FunctionUsageBucketDto> Buckets
);

public sealed record GetFunctionUsageRequest(
    [FromRoute] string Id,
    [FromQuery] DateTimeOffset? FromUtc,
    [FromQuery] DateTimeOffset? ToUtc,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetFunctionUsageOutcome>, ITenantReadOperation;

public union GetFunctionUsageOutcome(FunctionUsageResponse, FunctionAppNotFound);

public sealed class GetFunctionUsageHandler(
    LoadFunctionAppById loadFunctionAppById,
    LoadFunctionUsage loadFunctionUsage
) : IMediatorOutcomeHandler<GetFunctionUsageRequest, GetFunctionUsageOutcome>
{
    public async Task<GetFunctionUsageOutcome> HandleAsync(
        GetFunctionUsageRequest request,
        CancellationToken cancellationToken = default)
    {
        var app = await loadFunctionAppById(request.TenantId, request.Id, cancellationToken);
        if (app is null)
            return new FunctionAppNotFound(request.Id);

        var usage = await loadFunctionUsage(app, request.FromUtc, request.ToUtc, cancellationToken);
        return new FunctionUsageResponse(
            usage.AggregatedThroughUtc,
            usage.Buckets
                .Select(bucket => new FunctionUsageBucketDto(
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
                    bucket.ExecutionDurationMilliseconds))
                .ToArray());
    }
}

public sealed class GetFunctionUsageRequestValidator : AbstractValidator<GetFunctionUsageRequest>
{
    public GetFunctionUsageRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Function App ID");
        RuleFor(request => request)
            .Must(request => request.FromUtc is not { Offset: var fromOffset } || fromOffset == TimeSpan.Zero)
            .WithMessage("Usage range start must use UTC.");
        RuleFor(request => request)
            .Must(request => request.ToUtc is not { Offset: var toOffset } || toOffset == TimeSpan.Zero)
            .WithMessage("Usage range end must use UTC.");
        RuleFor(request => request)
            .Must(request => request.FromUtc is null || request.ToUtc is null || request.FromUtc <= request.ToUtc)
            .WithMessage("Usage range must be ordered.");
    }
}
