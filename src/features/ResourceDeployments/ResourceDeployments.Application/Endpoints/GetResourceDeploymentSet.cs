// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ResourceDeployments.Application.Endpoints;

public union GetResourceDeploymentSetOutcome(PlacementSummaryDto[]);

public sealed record GetResourceDeploymentSetRequest(
    [FromQuery] string ResourceType,
    [FromQuery] string ResourceId,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetResourceDeploymentSetOutcome>, ITenantReadOperation;

public sealed class GetResourceDeploymentSetValidator : AbstractValidator<GetResourceDeploymentSetRequest>
{
    public GetResourceDeploymentSetValidator()
    {
        RuleFor(request => request.ResourceType).Must(value => ResourceTypeId.TryCreate(value, out _));
        RuleFor(request => request.ResourceId).MongoObjectId("Resource ID");
    }
}

public sealed class GetResourceDeploymentSetHandler(
    LoadResourceDeploymentSet load
) : IMediatorOutcomeHandler<GetResourceDeploymentSetRequest, GetResourceDeploymentSetOutcome>
{
    public async Task<GetResourceDeploymentSetOutcome> HandleAsync(
        GetResourceDeploymentSetRequest request,
        CancellationToken cancellationToken = default) => (await load(
        new LoadResourceDeploymentSetRequest(
            TenantId: request.TenantId,
            ResourceType: ResourceTypeId.Create(request.ResourceType),
            ResourceId: ObjectId.Parse(request.ResourceId).ToString()),
        cancellationToken: cancellationToken)).ToArray();
}

public static class GetResourceDeploymentSetEndpoint
{
    public static IEndpointRouteBuilder MapGetResourceDeploymentSetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/",
                async (
                        IMediator mediator, [AsParameters] GetResourceDeploymentSetRequest request,
                        CancellationToken cancellationToken) =>
                    (await mediator.Send(request, cancellationToken)).ToHttpResult(outcome => outcome switch
                    {
                        PlacementSummaryDto[] placements => Results.Ok(placements),
                    }))
            .WithName("GetResourceDeploymentSet");
        return app;
    }
}
