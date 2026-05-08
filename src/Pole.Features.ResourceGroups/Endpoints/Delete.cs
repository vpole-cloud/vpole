using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.ResourceGroups.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Endpoints;

public static class Delete
{
    public static IEndpointRouteBuilder MapDeleteResourceGroupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async ( IMediator mediator, [AsParameters] DeleteResourceGroupRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(_ => Results.NoContent());
        })
        .WithName("DeleteResourceGroup")
        .WithSummary("Deletes a resource group by its ID.")
        .WithDescription("Deletes the specified resource group. Returns 204 No Content if successful, or 404 Not Found if the resource group does not exist.")
        .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}

public sealed record DeleteResourceGroupRequest(
    [FromRoute] string Id
) : IMediatorRequest<DeleteResourceGroupResponse>;

public record DeleteResourceGroupResponse(bool Deleted);

public sealed class DeleteResourceGroupHandler(
    MongoDbContext dbContext
    ) : IMediatorHandler<DeleteResourceGroupRequest, DeleteResourceGroupResponse>
{
    public async Task<Result<DeleteResourceGroupResponse>> HandleAsync(
        DeleteResourceGroupRequest request, CancellationToken cancellationToken = default)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(rg => rg.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (resourceGroup is null)
            return Result.NotFound($"Resource group with ID '{request.Id}' not found.");

        return await dbContext.RunInTransactionAsync<DeleteResourceGroupResponse>(async (sh, ct) =>
        {
            await dbContext.ResourceGroups.DeleteOneAsync(
                session: sh,
                filter: rg => rg.Id == request.Id,
                cancellationToken: ct);

            await dbContext.StoreEventAsync(resourceGroup.ToDeleteEvent(), sh, ct);

            return new DeleteResourceGroupResponse(Deleted: true);
        }, cancellationToken: cancellationToken);
    }
}

public class DeleteResourceGroupEventConsumer(
    IKubernetes kubernetes
    ) : IConsumer<DeleteEvent>
{
    public async Task Consume(ConsumeContext<DeleteEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        await kubernetes.CoreV1.DeleteNamespaceAsync(
            name: message.KubernetesNamespace,
            cancellationToken: ct);
    }
}
