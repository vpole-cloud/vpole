// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ResourceDeployments.Application.Endpoints;

public sealed record GetResourceDeploymentObservationRequest(
    [FromRoute] string DeploymentId,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetResourceDeploymentObservationOutcome>, ITenantReadOperation;

public union GetResourceDeploymentObservationOutcome(ResourceDeploymentObservationDto, ResourceDeploymentNotFound);

public sealed class GetResourceDeploymentObservationValidator
    : AbstractValidator<GetResourceDeploymentObservationRequest>
{
    public GetResourceDeploymentObservationValidator()
    {
        RuleFor(request => request.DeploymentId).MongoObjectId("Deployment ID");
        RuleFor(request => request.TenantId).MongoObjectId(TenantHeaders.TenantId);
    }
}

public sealed class GetResourceDeploymentObservationHandler(
    ResourceDeploymentOperationsPersistence persistence,
    ObserveResourceDeployment observe
) : IMediatorOutcomeHandler<GetResourceDeploymentObservationRequest, GetResourceDeploymentObservationOutcome>
{
    public async Task<GetResourceDeploymentObservationOutcome> HandleAsync(
        GetResourceDeploymentObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        var deployment = await persistence.LoadAsync(
            request.TenantId,
            request.DeploymentId.ToLowerInvariant(),
            cancellationToken);
        if (deployment is null)
            return new ResourceDeploymentNotFound(request.DeploymentId);
        return await observe.InvokeAsync(deployment, cancellationToken);
    }
}

public static class GetResourceDeploymentObservationEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceDeploymentObservationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{DeploymentId}/observation",
                async (
                        IMediator mediator, [AsParameters] GetResourceDeploymentObservationRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        ResourceDeploymentObservationDto observation => Results.Ok(observation),
                        ResourceDeploymentNotFound => Results.NotFound(),
                    }))
            .WithName("GetResourceDeploymentObservation");
        return app;
    }
}
