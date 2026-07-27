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

public sealed record GetRuntimeCellsRequest(
    [FromRoute] string ResourceGroupId,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null)
    : IMediatorRequest<IReadOnlyList<RuntimeCellDto>>, ITenantReadOperation;

public sealed class GetRuntimeCellsHandler(IResourceGroupDbContext dbContext)
    : IMediatorHandler<GetRuntimeCellsRequest, IReadOnlyList<RuntimeCellDto>>
{
    public async Task<Result<IReadOnlyList<RuntimeCellDto>>> HandleAsync(
        GetRuntimeCellsRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(Builders<ResourceGroup>.Filter.And(
                Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
                Builders<ResourceGroup>.Filter.Eq(group => group.Id, request.ResourceGroupId)))
            .FirstOrDefaultAsync(cancellationToken);
        if (resourceGroup is null)
            return Failures.NotFound("Resource group not found.", request.ResourceGroupId);

        var runtimeCells = await dbContext.RuntimeCells
            .Find(cell =>
                cell.ResourceGroupId == resourceGroup.Id &&
                cell.TenantId == resourceGroup.TenantId)
            .SortBy(cell => cell.IsolationScopeId)
            .ThenBy(cell => cell.DeploymentTargetId)
            .ToListAsync(cancellationToken);
        return runtimeCells.Select(cell => cell.ToDto()).ToList();
    }
}

public sealed class GetRuntimeCellsRequestValidator
    : AbstractValidator<GetRuntimeCellsRequest>
{
    public GetRuntimeCellsRequestValidator() =>
        RuleFor(request => request.ResourceGroupId)
            .Must(id => ObjectId.TryParse(id, out _))
            .WithMessage("Resource group ID must be a MongoDB ObjectId.");
}

public static class GetRuntimeCellsEndpoint
{
    public static IEndpointRouteBuilder MapGetRuntimeCellsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ResourceGroupId}/runtime-cells", async (
                IMediator mediator,
                [AsParameters] GetRuntimeCellsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetRuntimeCells")
            .WithSummary("Lists the runtime cells owned by a resource group.")
            .Produces<IReadOnlyList<RuntimeCellDto>>();

        return app;
    }
}
