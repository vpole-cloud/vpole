using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Mediator;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record GetDeploymentTargetsRequest
    : IMediatorRequest<GetDeploymentTargetsOutcome>;

public union GetDeploymentTargetsOutcome(
    IReadOnlyCollection<DeploymentTargetDto>);

public sealed class GetDeploymentTargetsHandler(
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    LoadDeploymentTargets loadDeploymentTargets)
    : IMediatorOutcomeHandler<GetDeploymentTargetsRequest, GetDeploymentTargetsOutcome>
{
    public async Task<GetDeploymentTargetsOutcome> HandleAsync(
        GetDeploymentTargetsRequest request,
        CancellationToken cancellationToken = default)
    {
        var defaultId = await loadDefaultDeploymentTargetId(cancellationToken);
        var targets = await loadDeploymentTargets(cancellationToken);
        return targets
            .Select(target => target.ToDto(target.Id == defaultId))
            .ToArray();
    }
}

public static class GetDeploymentTargetsEndpoint
{
    public static IEndpointRouteBuilder MapGetDeploymentTargetsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(new GetDeploymentTargetsRequest(), cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                IReadOnlyCollection<DeploymentTargetDto> targets => Results.Ok(targets)
            });
        })
            .WithName("GetDeploymentTargets")
            .Produces<IReadOnlyCollection<DeploymentTargetDto>>();
        return app;
    }
}
