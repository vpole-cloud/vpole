using k8s;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Features.ResourceGroups.Endpoints;

public static class Delete
{
    public static RouteGroupBuilder MapDeleteResourceGroupEndpoints(this RouteGroupBuilder group)
    {
        group.MapDelete("/{Id}", async ( IMediator mediator, [AsParameters] DeleteResourceGroupRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(_ => Results.NoContent());
        })
        .WithName("DeleteResourceGroup")
        .WithSummary("Deletes a resource group by its ID.")
        .WithDescription("Deletes the specified resource group. Returns 204 No Content if successful, or 404 Not Found if the resource group does not exist.")
        .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}

public sealed record DeleteResourceGroupRequest(
    [FromRoute] string Id
) : IMediatorRequest<DeleteResourceGroupResponse>;

public record DeleteResourceGroupResponse(bool Deleted);

public sealed class DeleteResourceGroupHandler(
    IKubernetes kubernetes,
    MongoDbContext mongoDbContext
    ) : IMediatorHandler<DeleteResourceGroupRequest, DeleteResourceGroupResponse>
{
    public async Task<Result<DeleteResourceGroupResponse>> HandleAsync(
        DeleteResourceGroupRequest request, CancellationToken cancellationToken = default)
    {
        var resourceGroup = await mongoDbContext.ResourceGroups
            .Find(rg => rg.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (resourceGroup is null)
            return Result.NotFound($"Resource group with ID '{request.Id}' not found.");

        await kubernetes.CoreV1.DeleteNamespaceAsync(resourceGroup.NamespaceName, cancellationToken: cancellationToken);
        
        await mongoDbContext.ResourceGroups.DeleteOneAsync(rg => rg.Id == request.Id, cancellationToken: cancellationToken);
        
        return new DeleteResourceGroupResponse(Deleted: true);
    }
}