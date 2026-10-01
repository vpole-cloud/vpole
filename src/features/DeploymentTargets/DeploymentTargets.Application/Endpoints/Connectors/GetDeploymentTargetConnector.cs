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

public sealed record GetDeploymentTargetConnectorRequest(
    [FromRoute] string Id,
    [FromRoute] string ConnectorId)
    : IMediatorRequest<GetDeploymentTargetConnectorOutcome>;

public union GetDeploymentTargetConnectorOutcome(
    DeploymentTargetConnectorDto,
    DeploymentTargetConnectorNotFound);

public sealed class GetDeploymentTargetConnectorHandler(
    LoadDeploymentTargetConnectorStatus loadStatus)
    : IMediatorOutcomeHandler<GetDeploymentTargetConnectorRequest, GetDeploymentTargetConnectorOutcome>
{
    public async Task<GetDeploymentTargetConnectorOutcome> HandleAsync(
        GetDeploymentTargetConnectorRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await loadStatus(request.Id, request.ConnectorId, cancellationToken);
        return result switch
        {
            DeploymentTargetConnectorDto status => status,
            DeploymentTargetConnectorNotFound notFound => notFound
        };
    }
}

public sealed class GetDeploymentTargetConnectorRequestValidator
    : AbstractValidator<GetDeploymentTargetConnectorRequest>
{
    public GetDeploymentTargetConnectorRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Deployment target ID");
        RuleFor(request => request.ConnectorId).MongoObjectId("Connector ID");
    }
}

public static class GetDeploymentTargetConnectorEndpoint
{
    public static IEndpointRouteBuilder MapGetDeploymentTargetConnectorEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/connectors/{ConnectorId}", async (
                IMediator mediator,
                [AsParameters] GetDeploymentTargetConnectorRequest request,
                CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                DeploymentTargetConnectorDto status => Results.Ok(status),
                DeploymentTargetConnectorNotFound error => DeploymentTargetHttpMappings.Map(error)
            });
        })
            .WithName("GetDeploymentTargetConnector")
            .Produces<DeploymentTargetConnectorDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}
