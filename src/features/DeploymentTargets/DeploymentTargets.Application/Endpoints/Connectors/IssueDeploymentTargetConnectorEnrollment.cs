using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.Connectors;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.DeploymentTargets.Application.Endpoints.Connectors;

public sealed record IssueDeploymentTargetConnectorEnrollmentBody(long TargetRevision);

public sealed record IssueDeploymentTargetConnectorEnrollmentRequest(
    [FromRoute] string Id,
    [FromBody] IssueDeploymentTargetConnectorEnrollmentBody Body)
    : IMediatorRequest<IssueDeploymentTargetConnectorEnrollmentOutcome>;

public union IssueDeploymentTargetConnectorEnrollmentOutcome(
    DeploymentTargetConnectorEnrollmentIssued,
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict,
    DeploymentTargetDeletionRejected);

public sealed class IssueDeploymentTargetConnectorEnrollmentHandler(
    IssueDeploymentTargetConnectorEnrollment issueEnrollment)
    : IMediatorOutcomeHandler<
        IssueDeploymentTargetConnectorEnrollmentRequest,
        IssueDeploymentTargetConnectorEnrollmentOutcome>
{
    public async Task<IssueDeploymentTargetConnectorEnrollmentOutcome> HandleAsync(
        IssueDeploymentTargetConnectorEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await issueEnrollment(request.Id, request.Body.TargetRevision, cancellationToken);
        return result switch
        {
            DeploymentTargetConnectorEnrollmentIssued issued => issued,
            DeploymentTargetNotFound notFound => notFound,
            DeploymentTargetRevisionConflict conflict => conflict,
            DeploymentTargetDeletionRejected deleting => deleting
        };
    }
}

public sealed class IssueDeploymentTargetConnectorEnrollmentRequestValidator
    : AbstractValidator<IssueDeploymentTargetConnectorEnrollmentRequest>
{
    public IssueDeploymentTargetConnectorEnrollmentRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Deployment target ID");
        RuleFor(request => request.Body).NotNull();
        RuleFor(request => request.Body.TargetRevision).GreaterThan(0)
            .When(request => request.Body is not null);
    }
}

public static class IssueDeploymentTargetConnectorEnrollmentEndpoint
{
    public static IEndpointRouteBuilder MapIssueDeploymentTargetConnectorEnrollmentEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/connectors/enrollments", async (
                IMediator mediator,
                HttpContext httpContext,
                [AsParameters] IssueDeploymentTargetConnectorEnrollmentRequest request,
                CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                DeploymentTargetConnectorEnrollmentIssued issued => Results.Ok(issued),
                DeploymentTargetNotFound error => DeploymentTargetHttpMappings.Map(error),
                DeploymentTargetRevisionConflict error => DeploymentTargetHttpMappings.Map(error),
                DeploymentTargetDeletionRejected error => DeploymentTargetHttpMappings.Map(error)
            });
        })
            .WithName("IssueDeploymentTargetConnectorEnrollment")
            .Produces<DeploymentTargetConnectorEnrollmentIssued>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
