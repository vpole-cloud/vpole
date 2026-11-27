// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record DeleteDeploymentTargetRequest(
    [FromRoute] string Id
) : IMediatorRequest<DeleteDeploymentTargetOutcome>;

public union DeleteDeploymentTargetOutcome(DeploymentTargetDeleted, DeleteDeploymentTargetError);

public sealed class DeleteDeploymentTargetHandler(
    DeleteDeploymentTarget deleteDeploymentTarget,
    InvalidateDeploymentTargetKubernetesClient invalidateDeploymentTargetKubernetesClient
) : IMediatorOutcomeHandler<DeleteDeploymentTargetRequest, DeleteDeploymentTargetOutcome>
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
            DeleteDeploymentTargetError error => new DeleteDeploymentTargetOutcome(error),
        };
    }
}

public sealed class DeleteDeploymentTargetRequestValidator : AbstractValidator<DeleteDeploymentTargetRequest>
{
    public DeleteDeploymentTargetRequestValidator() => RuleFor(request => request.Id)
        .MongoObjectId("Deployment target ID");
}

public static class DeleteDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapDeleteDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete(
                "/{Id}",
                async (
                    IMediator mediator, [AsParameters] DeleteDeploymentTargetRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        DeploymentTargetDeleted => Results.NoContent(),
                        DeleteDeploymentTargetError error => DeploymentTargetHttpMappings.Map(error),
                    });
                })
            .WithName("DeleteDeploymentTarget")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
