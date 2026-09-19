using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record RetryRuntimeCellRequest(
    [FromRoute] string ResourceGroupId,
    [FromRoute] string CellId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<RetryRuntimeCellOutcome>, ITenantWriteOperation;

public union RetryRuntimeCellError(
    RuntimeCellNotFound,
    RuntimeCellRetryRejected,
    ResourceGroupRevisionConflict);

public union RetryRuntimeCellOutcome(
    RuntimeCellDto,
    RetryRuntimeCellError);

public sealed class RetryRuntimeCellHandler(
    PersistRuntimeCellRetry persistRuntimeCellRetry)
    : IMediatorOutcomeHandler<RetryRuntimeCellRequest, RetryRuntimeCellOutcome>
{
    public async Task<RetryRuntimeCellOutcome> HandleAsync(
        RetryRuntimeCellRequest request,
        CancellationToken cancellationToken = default)
    {
        var retried = await persistRuntimeCellRetry(
            request.TenantId,
            request.ResourceGroupId,
            request.CellId,
            cancellationToken);
        return retried switch
        {
            RuntimeCell runtimeCell => runtimeCell.ToDto(),
            RuntimeCellNotFound error => new RetryRuntimeCellError(error),
            RuntimeCellRetryRejected error => new RetryRuntimeCellError(error),
            ResourceGroupRevisionConflict error => new RetryRuntimeCellError(error)
        };
    }
}

public sealed class RetryRuntimeCellRequestValidator
    : AbstractValidator<RetryRuntimeCellRequest>
{
    public RetryRuntimeCellRequestValidator()
    {
        RuleFor(request => request.ResourceGroupId)
            .MongoObjectId("Resource group ID");
        RuleFor(request => request.CellId)
            .MongoObjectId("Runtime cell ID");
    }
}

public static class RetryRuntimeCellEndpoint
{
    public static IEndpointRouteBuilder MapRetryRuntimeCellEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{ResourceGroupId}/runtime-cells/{CellId}/retry", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] RetryRuntimeCellRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    RuntimeCellDto runtimeCell => Results.Accepted(value: runtimeCell),
                    RetryRuntimeCellError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("RetryRuntimeCell")
            .WithSummary("Requests another attempt for a failed runtime cell.")
            .Produces<RuntimeCellDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
