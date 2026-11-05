using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record GetRuntimeCellRequest(
    [FromRoute] string ResourceGroupId,
    [FromRoute] string CellId,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetRuntimeCellOutcome>, ITenantReadOperation;

public union GetRuntimeCellOutcome(
    RuntimeCellDto,
    ResourceGroupNotFound,
    RuntimeCellNotFound);

public sealed class GetRuntimeCellHandler(
    LoadResourceGroup loadResourceGroup,
    LoadRuntimeCell loadRuntimeCell)
    : IMediatorOutcomeHandler<GetRuntimeCellRequest, GetRuntimeCellOutcome>
{
    public async Task<GetRuntimeCellOutcome> HandleAsync(
        GetRuntimeCellRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await loadResourceGroup(
            request.TenantId,
            request.ResourceGroupId,
            cancellationToken);
        if (resourceGroup is null)
            return new ResourceGroupNotFound(request.ResourceGroupId);

        var runtimeCell = await loadRuntimeCell(
            resourceGroup.TenantId,
            resourceGroup.Id,
            request.CellId,
            cancellationToken);
        return runtimeCell is null
            ? new RuntimeCellNotFound(request.CellId)
            : runtimeCell.ToDto();
    }
}

public sealed class GetRuntimeCellRequestValidator
    : AbstractValidator<GetRuntimeCellRequest>
{
    public GetRuntimeCellRequestValidator()
    {
        RuleFor(request => request.ResourceGroupId)
            .MongoObjectId("Resource group ID");
        RuleFor(request => request.CellId)
            .MongoObjectId("Runtime cell ID");
    }
}

public static class GetRuntimeCellEndpoint
{
    public static IEndpointRouteBuilder MapGetRuntimeCellEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ResourceGroupId}/runtime-cells/{CellId}", async (
                IMediator mediator,
                [AsParameters] GetRuntimeCellRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    RuntimeCellDto runtimeCell => Results.Ok(runtimeCell),
                    ResourceGroupNotFound error => ResourceGroupHttpMappings.Map(error),
                    RuntimeCellNotFound error => ResourceGroupHttpMappings.Map(error)
                });
            })
            .WithName("GetRuntimeCell")
            .WithSummary("Gets a runtime cell owned by a resource group.")
            .Produces<RuntimeCellDto>();

        return app;
    }
}
