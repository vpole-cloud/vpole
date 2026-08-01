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
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.ResourceDeployments.Application.Endpoints;

public sealed record RetryResourceDeploymentRequest(
    [FromRoute] string DeploymentId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<ResourceDeploymentDto>, ITenantWriteOperation;

public sealed class RetryResourceDeploymentHandler(
    IResourceDeploymentDbContext dbContext,
    TimeProvider timeProvider)
    : IMediatorHandler<RetryResourceDeploymentRequest, ResourceDeploymentDto>
{
    public Task<Result<ResourceDeploymentDto>> HandleAsync(
        RetryResourceDeploymentRequest request,
        CancellationToken cancellationToken = default) =>
        dbContext.RunInTransactionAsync<ResourceDeploymentDto>(
            async (session, ct) =>
            {
                var filter = Builders<ResourceDeployment>.Filter.And(
                    Builders<ResourceDeployment>.Filter.Eq(
                        deployment => deployment.Id,
                        request.DeploymentId),
                    Builders<ResourceDeployment>.Filter.ForTenant(request.TenantId));
                var current = await dbContext.ResourceDeployments
                    .Find(session, filter)
                    .FirstOrDefaultAsync(ct);
                if (current is null)
                {
                    return Failures.NotFound(
                        "Resource deployment not found.",
                        request.DeploymentId);
                }
                if (current.State is not FailedResourceDeploymentState
                    {
                        Retryable: true
                    })
                {
                    return Failures.Conflict(
                        "Only a retryable failed resource deployment can be retried.");
                }

                var retried = current.Retry(timeProvider.GetUtcNow().UtcDateTime);
                var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
                    session,
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
                    return Failures.Conflict(
                        "The resource deployment changed while retry was being requested.");
                }

                await dbContext.StoreEventAsync(
                    retried.ToCurrentRequestWrite(),
                    session,
                    ct);
                return retried.ToDto();
            },
            cancellationToken: cancellationToken);
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
                [AsParameters] RetryResourceDeploymentRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(
                    deployment => Results.Accepted(value: deployment));
            })
            .WithName("RetryResourceDeployment")
            .WithSummary("Requests another attempt for a failed resource deployment.")
            .Produces<ResourceDeploymentDto>(StatusCodes.Status202Accepted);

        return app;
    }
}
