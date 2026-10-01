using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.Connectors;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.DeploymentTargets.Application.Endpoints.Connectors;

public sealed record RevokeDeploymentTargetConnectorBody(long ConnectorRevision);

public sealed record RevokeDeploymentTargetConnectorRequest(
    [FromRoute] string Id,
    [FromRoute] string ConnectorId,
    [FromBody] RevokeDeploymentTargetConnectorBody Body)
    : IMediatorRequest<RevokeDeploymentTargetConnectorOutcome>;

public union RevokeDeploymentTargetConnectorOutcome(
    DeploymentTargetConnectorRevoked,
    DeploymentTargetConnectorNotFound,
    DeploymentTargetConnectorRevisionConflict);

public sealed class RevokeDeploymentTargetConnectorHandler(
    RevokeDeploymentTargetConnector revokeConnector)
    : IMediatorOutcomeHandler<RevokeDeploymentTargetConnectorRequest, RevokeDeploymentTargetConnectorOutcome>
{
    public async Task<RevokeDeploymentTargetConnectorOutcome> HandleAsync(
        RevokeDeploymentTargetConnectorRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await revokeConnector(
            request.Id,
            request.ConnectorId,
            request.Body.ConnectorRevision,
            cancellationToken);
        return result switch
        {
            DeploymentTargetConnectorRevoked revoked => revoked,
            DeploymentTargetConnectorNotFound notFound => notFound,
            DeploymentTargetConnectorRevisionConflict conflict => conflict
        };
    }
}

public sealed class RevokeDeploymentTargetConnectorRequestValidator
    : AbstractValidator<RevokeDeploymentTargetConnectorRequest>
{
    public RevokeDeploymentTargetConnectorRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Deployment target ID");
        RuleFor(request => request.ConnectorId).MongoObjectId("Connector ID");
        RuleFor(request => request.Body).NotNull();
        RuleFor(request => request.Body.ConnectorRevision).GreaterThan(0)
            .When(request => request.Body is not null);
    }
}

public static class RevokeDeploymentTargetConnectorEndpoint
{
    public static IEndpointRouteBuilder MapRevokeDeploymentTargetConnectorEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/connectors/{ConnectorId}/revoke", async (
                IMediator mediator,
                [AsParameters] RevokeDeploymentTargetConnectorRequest request,
                CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                DeploymentTargetConnectorRevoked revoked => Results.Ok(revoked),
                DeploymentTargetConnectorNotFound error => DeploymentTargetHttpMappings.Map(error),
                DeploymentTargetConnectorRevisionConflict error => DeploymentTargetHttpMappings.Map(error)
            });
        })
            .WithName("RevokeDeploymentTargetConnector")
            .Produces<DeploymentTargetConnectorRevoked>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
