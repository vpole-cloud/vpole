using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record GetRuntimeCellRequest(
    [FromRoute] string ResourceGroupId,
    [FromRoute] string CellId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<RuntimeCellDto>, ITenantReadOperation;

public sealed class GetRuntimeCellHandler(IResourceGroupDbContext dbContext)
    : IMediatorHandler<GetRuntimeCellRequest, RuntimeCellDto>
{
    public async Task<Result<RuntimeCellDto>> HandleAsync(
        GetRuntimeCellRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(Builders<ResourceGroup>.Filter.And(
                Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
                Builders<ResourceGroup>.Filter.Eq(
                    group => group.Id,
                    request.ResourceGroupId)))
            .FirstOrDefaultAsync(cancellationToken);
        if (resourceGroup is null)
            return Failures.NotFound("Resource group not found.", request.ResourceGroupId);

        var runtimeCell = await dbContext.RuntimeCells
            .Find(cell =>
                cell.Id == request.CellId &&
                cell.ResourceGroupId == resourceGroup.Id &&
                cell.TenantId == resourceGroup.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        return runtimeCell is null
            ? Failures.NotFound("Runtime cell not found.", request.CellId)
            : runtimeCell.ToDto();
    }
}

public sealed class GetRuntimeCellRequestValidator
    : AbstractValidator<GetRuntimeCellRequest>
{
    public GetRuntimeCellRequestValidator()
    {
        RuleFor(request => request.ResourceGroupId)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Resource group ID must be a MongoDB ObjectId.");
        RuleFor(request => request.CellId)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Runtime cell ID must be a MongoDB ObjectId.");
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
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetRuntimeCell")
            .WithSummary("Gets a runtime cell owned by a resource group.")
            .Produces<RuntimeCellDto>();

        return app;
    }
}
