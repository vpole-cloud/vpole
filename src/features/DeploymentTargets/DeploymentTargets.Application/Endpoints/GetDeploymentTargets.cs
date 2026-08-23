using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Mediator;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record GetDeploymentTargetsRequest
    : IMediatorRequest<IReadOnlyCollection<DeploymentTargetDto>>;

public sealed class GetDeploymentTargetsHandler(
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    LoadDeploymentTargets loadDeploymentTargets)
    : IMediatorOutcomeHandler<GetDeploymentTargetsRequest, IReadOnlyCollection<DeploymentTargetDto>>
{
    public async Task<IReadOnlyCollection<DeploymentTargetDto>> HandleAsync(
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
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetDeploymentTargets")
            .Produces<IReadOnlyCollection<DeploymentTargetDto>>();
        return app;
    }
}
