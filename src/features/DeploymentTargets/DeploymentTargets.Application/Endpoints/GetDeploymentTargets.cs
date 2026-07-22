using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record GetDeploymentTargetsRequest
    : IMediatorRequest<IReadOnlyCollection<DeploymentTargetDto>>;

public sealed class GetDeploymentTargetsHandler(IDeploymentTargetDbContext dbContext)
    : IMediatorHandler<GetDeploymentTargetsRequest, IReadOnlyCollection<DeploymentTargetDto>>
{
    public async Task<Result<IReadOnlyCollection<DeploymentTargetDto>>> HandleAsync(
        GetDeploymentTargetsRequest request,
        CancellationToken cancellationToken = default)
    {
        var defaultId = await DeploymentTargetEndpointSupport.GetDefaultIdAsync(dbContext, cancellationToken);
        var targets = await dbContext.DeploymentTargets
            .Find(Builders<DeploymentTarget>.Filter.Empty)
            .SortBy(target => target.DisplayName)
            .ToListAsync(cancellationToken);
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
