using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Persistence.Mutations;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.Functions.Application.Endpoints;

public static class RetryFunctionInvocation
{
    public static IEndpointRouteBuilder MapRetryFunctionInvocationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/invocations/{InvocationId}:retry", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] RetryFunctionInvocationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    RetryFunctionInvocationResponse response => Results.Accepted(value: response),
                    RetryFunctionInvocationError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("RetryFunctionInvocation")
            .Produces<RetryFunctionInvocationResponse>(StatusCodes.Status202Accepted);
        return app;
    }
}

public sealed record RetryFunctionInvocationRequest(
    [FromRoute] string Id,
    [FromRoute] string InvocationId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<RetryFunctionInvocationOutcome>, ITenantWriteOperation;

public sealed record RetryFunctionInvocationResponse(
    string InvocationId,
    int NextAttemptNumber);

public union RetryFunctionInvocationOutcome(
    RetryFunctionInvocationResponse,
    RetryFunctionInvocationError);

public sealed class RetryFunctionInvocationHandler(
    Persistence.Mutations.RetryFunctionInvocation retryFunctionInvocation,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<RetryFunctionInvocationRequest, RetryFunctionInvocationOutcome>
{
    public async Task<RetryFunctionInvocationOutcome> HandleAsync(
        RetryFunctionInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        var retried = await retryFunctionInvocation(
            request.TenantId,
            request.Id,
            request.InvocationId,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return retried.Match<
            FunctionInvocationManualRetry,
            RetryFunctionInvocationError,
            RetryFunctionInvocationOutcome>(
            value => new RetryFunctionInvocationResponse(
                value.InvocationId,
                value.NextAttemptNumber),
            error => error);
    }
}

public sealed class RetryFunctionInvocationRequestValidator
    : AbstractValidator<RetryFunctionInvocationRequest>
{
    public RetryFunctionInvocationRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Function App ID");
}
