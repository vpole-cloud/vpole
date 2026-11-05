using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Invocations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Revisions;
using Pole.Features.Functions.Application.Usage;
using Pole.Features.Functions.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Functions.Application.Common.FunctionAppHttpMappings;

namespace Pole.Features.Functions.Application.Endpoints;

public static class GetFunctionInvocations
{
    public static IEndpointRouteBuilder MapGetFunctionInvocationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/invocations", async (
                IMediator mediator,
                [AsParameters] GetFunctionInvocationsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyList<FunctionInvocationDto> invocations => Results.Ok(invocations),
                    FunctionAppNotFound notFound => MapNotFound(notFound)
                });
            })
            .WithName("GetFunctionInvocations")
            .Produces<IReadOnlyList<FunctionInvocationDto>>();
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
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetFunctionInvocationsOutcome>, ITenantReadOperation;

public union GetFunctionInvocationsOutcome(
    IReadOnlyList<FunctionInvocationDto>,
    FunctionAppNotFound);

public sealed class GetFunctionInvocationsHandler(
    LoadFunctionAppById loadFunctionAppById,
    LoadFunctionInvocations loadFunctionInvocations)
    : IMediatorOutcomeHandler<GetFunctionInvocationsRequest, GetFunctionInvocationsOutcome>
{
    public async Task<GetFunctionInvocationsOutcome> HandleAsync(
        GetFunctionInvocationsRequest request,
        CancellationToken cancellationToken = default)
    {
        var app = await loadFunctionAppById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (app is null)
            return new FunctionAppNotFound(request.Id);
        var take = request.Take == 0 ? 50 : Math.Clamp(request.Take, 1, 200);
        var invocations = await loadFunctionInvocations(app, take, cancellationToken);
        return invocations.Select(FunctionInvocationDtoFactory.Create).ToArray();
    }
}

public sealed class GetFunctionInvocationsRequestValidator
    : AbstractValidator<GetFunctionInvocationsRequest>
{
    public GetFunctionInvocationsRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Function App ID");
        RuleFor(request => request.Take).InclusiveBetween(0, 200);
    }
}

file static class FunctionInvocationDtoFactory
{
    public static FunctionInvocationDto Create(FunctionInvocation invocation) =>
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
