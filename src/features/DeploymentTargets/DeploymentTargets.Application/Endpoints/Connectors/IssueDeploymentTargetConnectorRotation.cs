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

public sealed record IssueDeploymentTargetConnectorRotationBody(long ConnectorRevision);

public sealed record IssueDeploymentTargetConnectorRotationRequest(
    [FromRoute] string Id,
    [FromRoute] string ConnectorId,
    [FromBody] IssueDeploymentTargetConnectorRotationBody Body)
    : IMediatorRequest<IssueDeploymentTargetConnectorRotationOutcome>;

public union IssueDeploymentTargetConnectorRotationOutcome(
    DeploymentTargetConnectorEnrollmentIssued,
    DeploymentTargetNotFound,
    DeploymentTargetDeletionRejected,
    DeploymentTargetConnectorNotFound,
    DeploymentTargetConnectorRevisionConflict);

public sealed class IssueDeploymentTargetConnectorRotationHandler(
    IssueDeploymentTargetConnectorRotation issueRotation)
    : IMediatorOutcomeHandler<
        IssueDeploymentTargetConnectorRotationRequest,
        IssueDeploymentTargetConnectorRotationOutcome>
{
    public async Task<IssueDeploymentTargetConnectorRotationOutcome> HandleAsync(
        IssueDeploymentTargetConnectorRotationRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await issueRotation(
            request.Id,
            request.ConnectorId,
            request.Body.ConnectorRevision,
            cancellationToken);
        return result switch
        {
            DeploymentTargetConnectorEnrollmentIssued issued => issued,
            DeploymentTargetNotFound notFound => notFound,
            DeploymentTargetDeletionRejected deleting => deleting,
            DeploymentTargetConnectorNotFound notFound => notFound,
            DeploymentTargetConnectorRevisionConflict conflict => conflict
        };
    }
}

public sealed class IssueDeploymentTargetConnectorRotationRequestValidator
    : AbstractValidator<IssueDeploymentTargetConnectorRotationRequest>
{
    public IssueDeploymentTargetConnectorRotationRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Deployment target ID");
        RuleFor(request => request.ConnectorId).MongoObjectId("Connector ID");
        RuleFor(request => request.Body).NotNull();
        RuleFor(request => request.Body.ConnectorRevision).GreaterThan(0)
            .When(request => request.Body is not null);
    }
}

public static class IssueDeploymentTargetConnectorRotationEndpoint
{
    public static IEndpointRouteBuilder MapIssueDeploymentTargetConnectorRotationEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/connectors/{ConnectorId}/enrollments", async (
                IMediator mediator,
                HttpContext httpContext,
                [AsParameters] IssueDeploymentTargetConnectorRotationRequest request,
                CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                DeploymentTargetConnectorEnrollmentIssued issued => Results.Ok(issued),
                DeploymentTargetNotFound error => DeploymentTargetHttpMappings.Map(error),
                DeploymentTargetDeletionRejected error => DeploymentTargetHttpMappings.Map(error),
                DeploymentTargetConnectorNotFound error => DeploymentTargetHttpMappings.Map(error),
                DeploymentTargetConnectorRevisionConflict error => DeploymentTargetHttpMappings.Map(error)
            });
        })
            .WithName("IssueDeploymentTargetConnectorRotation")
            .Produces<DeploymentTargetConnectorEnrollmentIssued>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
