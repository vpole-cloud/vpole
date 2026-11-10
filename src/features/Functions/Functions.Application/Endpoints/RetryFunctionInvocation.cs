using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Invocations;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.Functions.Application.Common.FunctionAppHttpMappings;

namespace Pole.Features.Functions.Application.Endpoints;

public static class RetryFunctionInvocation
{
    public static IEndpointRouteBuilder MapRetryFunctionInvocationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/{Id}/invocations/{InvocationId}:retry",
                async (
                    IMediator mediator, [AsParameters] RetryFunctionInvocationRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        RetryFunctionInvocationResponse response => Results.Accepted(value: response),
                        RetryFunctionInvocationError error => MapRetryError(error),
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
    [FromTenantHeader] string TenantId
) : IMediatorRequest<RetryFunctionInvocationOutcome>, ITenantWriteOperation;

public sealed record RetryFunctionInvocationResponse(
    string InvocationId,
    int NextAttemptNumber
);

public union RetryFunctionInvocationOutcome(RetryFunctionInvocationResponse, RetryFunctionInvocationError);

public sealed class RetryFunctionInvocationHandler(
    Invocations.RetryFunctionInvocation retryFunctionInvocation,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<RetryFunctionInvocationRequest, RetryFunctionInvocationOutcome>
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
        return retried switch
        {
            FunctionInvocationManualRetry value => new RetryFunctionInvocationResponse(
                value.InvocationId,
                value.NextAttemptNumber),
            RetryFunctionInvocationError error => error,
        };
    }
}

public sealed class RetryFunctionInvocationRequestValidator : AbstractValidator<RetryFunctionInvocationRequest>
{
    public RetryFunctionInvocationRequestValidator() => RuleFor(request => request.Id).MongoObjectId("Function App ID");
}
