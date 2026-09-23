using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.ResourceDeployments.Application.Endpoints;

public sealed record ResourceDeploymentNotFound(string DeploymentId);

public sealed record ResourceDeploymentNotRetryable(string DeploymentId);

public sealed record ResourceDeploymentRetryConflict(string DeploymentId);

public union RetryResourceDeploymentError(
    ResourceDeploymentNotFound,
    ResourceDeploymentNotRetryable,
    ResourceDeploymentRetryConflict);

public union RetryResourceDeploymentOutcome(
    ResourceDeploymentDto,
    RetryResourceDeploymentError);

/// <summary>
/// Retries one deployment inside a caller-owned transaction so the state
/// mutation and reconciliation outbox write commit atomically.
/// </summary>
public delegate Task<Result<ResourceDeploymentDto, RetryResourceDeploymentError>>
    RetryResourceDeployment(
        string tenantId,
        string deploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public sealed record RetryResourceDeploymentRequest(
    [FromRoute] string DeploymentId,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<RetryResourceDeploymentOutcome>, ITenantWriteOperation;

public sealed class RetryResourceDeploymentHandler(
    RunMongoOutcomeTransaction<RetryResourceDeploymentOutcome>
        runMongoTransaction,
    RetryResourceDeployment retryResourceDeployment)
    : IMediatorOutcomeHandler<
        RetryResourceDeploymentRequest,
        RetryResourceDeploymentOutcome>
{
    public Task<RetryResourceDeploymentOutcome> HandleAsync(
        RetryResourceDeploymentRequest request,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(
            async (transaction, ct) =>
            {
                var result = await retryResourceDeployment(
                    request.TenantId,
                    request.DeploymentId,
                    transaction.Session,
                    ct);
                return result.TryGetValue(out var deployment, out var error)
                    ? transaction.Commit(deployment)
                    : Abort(transaction, error);
            },
            cancellationToken);

    private static MongoTransactionDecision<RetryResourceDeploymentOutcome>
        Abort(
            MongoTransactionContext<RetryResourceDeploymentOutcome> transaction,
            RetryResourceDeploymentError error) =>
        transaction.Abort(error);
}

public sealed class RetryResourceDeploymentRequestValidator
    : AbstractValidator<RetryResourceDeploymentRequest>
{
    public RetryResourceDeploymentRequestValidator()
    {
        RuleFor(request => request.DeploymentId)
            .MongoObjectId("Resource deployment ID");
    }
}

public static class RetryResourceDeploymentEndpoint
{
    public static IEndpointRouteBuilder MapRetryResourceDeploymentEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/{DeploymentId}/retry", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] RetryResourceDeploymentRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(
                    outcome => outcome switch
                    {
                        ResourceDeploymentDto deployment =>
                            Results.Accepted(value: deployment),
                        RetryResourceDeploymentError error =>
                            httpProblems.Map(error).ToHttpResult()
                    });
            })
            .WithName("RetryResourceDeployment")
            .WithSummary("Requests another attempt for a failed resource deployment.")
            .Produces<ResourceDeploymentDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
