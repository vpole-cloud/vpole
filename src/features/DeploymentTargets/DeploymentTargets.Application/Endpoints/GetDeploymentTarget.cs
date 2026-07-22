using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record GetDeploymentTargetRequest(string Id)
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class GetDeploymentTargetHandler(IDeploymentTargetDbContext dbContext)
    : IMediatorHandler<GetDeploymentTargetRequest, DeploymentTargetDto>
{
    public async Task<Result<DeploymentTargetDto>> HandleAsync(
        GetDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await DeploymentTargetEndpointSupport.GetTargetAsync(
            dbContext,
            request.Id,
            cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var failure))
            return failure;
        var defaultId = await DeploymentTargetEndpointSupport.GetDefaultIdAsync(dbContext, cancellationToken);
        return target.ToDto(target.Id == defaultId);
    }
}

public static class GetDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapGetDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{id}", async (
                IMediator mediator,
                [FromRoute] string id,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new GetDeploymentTargetRequest(id), cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetDeploymentTarget")
            .Produces<DeploymentTargetDto>();
        return app;
    }
}
