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

public sealed record ListDeploymentTargetConnectorsRequest([FromRoute] string Id)
    : IMediatorRequest<ListDeploymentTargetConnectorsOutcome>;

public union ListDeploymentTargetConnectorsOutcome(
    DeploymentTargetConnectorList,
    DeploymentTargetNotFound);

public sealed class ListDeploymentTargetConnectorsHandler(
    LoadDeploymentTargetConnectorList loadConnectors)
    : IMediatorOutcomeHandler<ListDeploymentTargetConnectorsRequest, ListDeploymentTargetConnectorsOutcome>
{
    public async Task<ListDeploymentTargetConnectorsOutcome> HandleAsync(
        ListDeploymentTargetConnectorsRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await loadConnectors(request.Id, cancellationToken);
        return result switch
        {
            DeploymentTargetConnectorList list => list,
            DeploymentTargetNotFound notFound => notFound
        };
    }
}

public sealed class ListDeploymentTargetConnectorsRequestValidator
    : AbstractValidator<ListDeploymentTargetConnectorsRequest>
{
    public ListDeploymentTargetConnectorsRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Deployment target ID");
}

public static class ListDeploymentTargetConnectorsEndpoint
{
    public static IEndpointRouteBuilder MapListDeploymentTargetConnectorsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}/connectors", async (
                IMediator mediator,
                [AsParameters] ListDeploymentTargetConnectorsRequest request,
                CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                DeploymentTargetConnectorList list => Results.Ok(list),
                DeploymentTargetNotFound error => DeploymentTargetHttpMappings.Map(error)
            });
        })
            .WithName("ListDeploymentTargetConnectors")
            .Produces<DeploymentTargetConnectorList>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}
