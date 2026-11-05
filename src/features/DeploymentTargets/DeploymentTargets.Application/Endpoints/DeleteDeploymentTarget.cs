using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record DeleteDeploymentTargetRequest(
    [FromRoute] string Id)
    : IMediatorRequest<DeleteDeploymentTargetOutcome>;

public union DeleteDeploymentTargetOutcome(
    DeploymentTargetDeleted,
    DeleteDeploymentTargetError);

public sealed class DeleteDeploymentTargetHandler(
    DeleteDeploymentTarget deleteDeploymentTarget,
    InvalidateDeploymentTargetKubernetesClient invalidateDeploymentTargetKubernetesClient)
    : IMediatorOutcomeHandler<DeleteDeploymentTargetRequest, DeleteDeploymentTargetOutcome>
{
    public async Task<DeleteDeploymentTargetOutcome> HandleAsync(
        DeleteDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var outcome = await deleteDeploymentTarget(request.Id, cancellationToken);
        if (outcome is DeploymentTargetDeleted deleted)
            invalidateDeploymentTargetKubernetesClient(deleted.DeploymentTargetId);
        return outcome switch
        {
            DeploymentTargetDeleted value => new DeleteDeploymentTargetOutcome(value),
            DeleteDeploymentTargetError error => new DeleteDeploymentTargetOutcome(error)
        };
    }
}

public sealed class DeleteDeploymentTargetRequestValidator
    : AbstractValidator<DeleteDeploymentTargetRequest>
{
    public DeleteDeploymentTargetRequestValidator() => RuleFor(request => request.Id)
            .MongoObjectId("Deployment target ID");
}

public static class DeleteDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapDeleteDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                [AsParameters] DeleteDeploymentTargetRequest request,
                CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(outcome => outcome switch
            {
                DeploymentTargetDeleted => Results.NoContent(),
                DeleteDeploymentTargetError error => DeploymentTargetHttpMappings.Map(error)
            });
        })
            .WithName("DeleteDeploymentTarget")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
