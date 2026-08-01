using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record GetDeploymentTargetRequest(
    [FromRoute] string Id)
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class GetDeploymentTargetHandler(DeploymentTargetReader reader)
    : IMediatorHandler<GetDeploymentTargetRequest, DeploymentTargetDto>
{
    public async Task<Result<DeploymentTargetDto>> HandleAsync(
        GetDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await reader.GetAsync(request.Id, cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var failure))
            return failure;
        var defaultId = await reader.GetDefaultIdAsync(cancellationToken);
        return target.ToDto(target.Id == defaultId);
    }
}

public sealed class GetDeploymentTargetRequestValidator
    : AbstractValidator<GetDeploymentTargetRequest>
{
    public GetDeploymentTargetRequestValidator() =>
        RuleFor(request => request.Id)
            .MongoObjectId("Deployment target ID");
}

public static class GetDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapGetDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (
                IMediator mediator,
                [AsParameters] GetDeploymentTargetRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetDeploymentTarget")
            .Produces<DeploymentTargetDto>();
        return app;
    }
}
