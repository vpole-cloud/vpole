using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Mediator;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record GetDeploymentTargetRequest(
    [FromRoute] string Id)
    : IMediatorRequest<GetDeploymentTargetOutcome>;

public union GetDeploymentTargetOutcome(
    DeploymentTargetDto,
    DeploymentTargetNotFound);

public sealed class GetDeploymentTargetHandler(
    LoadDeploymentTargetById loadDeploymentTargetById,
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId)
    : IMediatorOutcomeHandler<GetDeploymentTargetRequest, GetDeploymentTargetOutcome>
{
    public async Task<GetDeploymentTargetOutcome> HandleAsync(
        GetDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await loadDeploymentTargetById(request.Id, cancellationToken);
        if (target is null)
            return new DeploymentTargetNotFound(request.Id);
        var defaultId = await loadDefaultDeploymentTargetId(cancellationToken);
        return target.ToDto(target.Id == defaultId);
    }
}

public sealed class GetDeploymentTargetRequestValidator
    : AbstractValidator<GetDeploymentTargetRequest>
{
    public GetDeploymentTargetRequestValidator() => RuleFor(request => request.Id)
            .MongoObjectId("Deployment target ID");
}

public static class GetDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapGetDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (
                IMediator mediator,
                [AsParameters] GetDeploymentTargetRequest request,
                CancellationToken cancellationToken) => {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    DeploymentTargetDto target => Results.Ok(target),
                    DeploymentTargetNotFound notFound => DeploymentTargetHttpMappings.Map(notFound)
                });
            })
            .WithName("GetDeploymentTarget")
            .Produces<DeploymentTargetDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }
}
