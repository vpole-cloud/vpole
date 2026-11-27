// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record GetRuntimeCellsRequest(
    [FromRoute] string ResourceGroupId,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetRuntimeCellsOutcome>, ITenantReadOperation;

public union GetRuntimeCellsOutcome(IReadOnlyList<RuntimeCellDto>, ResourceGroupNotFound);

public sealed class GetRuntimeCellsHandler(
    LoadResourceGroup loadResourceGroup,
    LoadRuntimeCells loadRuntimeCells
) : IMediatorOutcomeHandler<GetRuntimeCellsRequest, GetRuntimeCellsOutcome>
{
    public async Task<GetRuntimeCellsOutcome> HandleAsync(
        GetRuntimeCellsRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await loadResourceGroup(request.TenantId, request.ResourceGroupId, cancellationToken);
        if (resourceGroup is null)
            return new ResourceGroupNotFound(request.ResourceGroupId);

        var runtimeCells = await loadRuntimeCells(resourceGroup.TenantId, resourceGroup.Id, cancellationToken);
        return runtimeCells.Select(cell => cell.ToDto()).ToList();
    }
}

public sealed class GetRuntimeCellsRequestValidator : AbstractValidator<GetRuntimeCellsRequest>
{
    public GetRuntimeCellsRequestValidator() =>
        RuleFor(request => request.ResourceGroupId).MongoObjectId("Resource group ID");
}

public static class GetRuntimeCellsEndpoint
{
    public static IEndpointRouteBuilder MapGetRuntimeCellsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/{ResourceGroupId}/runtime-cells",
                async (
                    IMediator mediator, [AsParameters] GetRuntimeCellsRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        IReadOnlyList<RuntimeCellDto> runtimeCells => Results.Ok(runtimeCells),
                        ResourceGroupNotFound error => ResourceGroupHttpMappings.Map(error),
                    });
                })
            .WithName("GetRuntimeCells")
            .WithSummary("Lists the runtime cells owned by a resource group.")
            .Produces<IReadOnlyList<RuntimeCellDto>>();

        return app;
    }
}
