using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Mediator;
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

public sealed record RetryResourceDeploymentRequest(
    [FromRoute] string DeploymentId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<RetryResourceDeploymentOutcome>, ITenantWriteOperation;

public sealed class RetryResourceDeploymentHandler(
    RunMongoOutcomeTransaction<RetryResourceDeploymentOutcome>
        runMongoTransaction,
    IResourceDeploymentDbContext dbContext,
    TimeProvider timeProvider)
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
                var filter = Builders<ResourceDeployment>.Filter.And(
                    Builders<ResourceDeployment>.Filter.Eq(
                        deployment => deployment.Id,
                        request.DeploymentId),
                    Builders<ResourceDeployment>.Filter.ForTenant(request.TenantId));
                var current = await dbContext.ResourceDeployments
                    .Find(transaction.Session, filter)
                    .FirstOrDefaultAsync(ct);
                if (current is null)
                {
                    return Abort(
                        transaction,
                        new ResourceDeploymentNotFound(
                            request.DeploymentId));
                }
                if (current.State is not FailedResourceDeploymentState
                    {
                        Retryable: true
                    })
                {
                    return Abort(
                        transaction,
                        new ResourceDeploymentNotRetryable(
                            request.DeploymentId));
                }

                var retried = current.Retry(timeProvider.GetUtcNow().UtcDateTime);
                var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
                    transaction.Session,
                    Builders<ResourceDeployment>.Filter.And(
                        filter,
                        Builders<ResourceDeployment>.Filter.Eq(
                            deployment => deployment.Revision,
                            current.Revision),
                        Builders<ResourceDeployment>.Filter
                            .OfType<ResourceDeploymentState, FailedResourceDeploymentState>(
                                deployment => deployment.State)),
                    retried,
                    cancellationToken: ct);
                if (replacement.ModifiedCount != 1)
                {
                    return Abort(
                        transaction,
                        new ResourceDeploymentRetryConflict(
                            request.DeploymentId));
                }

                await dbContext.StoreEventAsync(
                    retried.ToCurrentRequestWrite(),
                    transaction.Session,
                    ct);
                return transaction.Commit(retried.ToDto());
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
