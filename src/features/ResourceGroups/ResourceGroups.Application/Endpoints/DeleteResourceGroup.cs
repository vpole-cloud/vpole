using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class DeleteResourceGroup
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
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null
) : IMediatorRequest<DeleteResourceGroupResponse>, ITenantWriteOperation;

public record DeleteResourceGroupResponse(bool Deleted);

public sealed class DeleteResourceGroupHandler(
    IResourceGroupDbContext dbContext
    ) : IMediatorHandler<DeleteResourceGroupRequest, DeleteResourceGroupResponse>
{
    public async Task<Result<DeleteResourceGroupResponse>> HandleAsync(
        DeleteResourceGroupRequest request, CancellationToken cancellationToken = default)
    {
        var filter = Builders<ResourceGroup>.Filter.And(
            Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
            Builders<ResourceGroup>.Filter.Eq(rg => rg.Id, request.Id));

        var resourceGroup = await dbContext.ResourceGroups
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (resourceGroup is null)
            return Failures.NotFound("Resource group not found.", request.Id);

        return await dbContext.RunInTransactionAsync<DeleteResourceGroupResponse>(async (sh, ct) =>
        {
            await dbContext.IsolationScopes.DeleteManyAsync(
                session: sh,
                filter: scope =>
                    scope.TenantId == resourceGroup.TenantId &&
                    scope.ResourceGroupId == resourceGroup.Id,
                cancellationToken: ct);

            await dbContext.ResourceGroups.DeleteOneAsync(
                session: sh,
                filter: filter,
                cancellationToken: ct);

            await dbContext.StoreEventAsync(resourceGroup.ToDeleteEvent(), sh, ct);

            return new DeleteResourceGroupResponse(Deleted: true);
        }, cancellationToken: cancellationToken);
    }
}

public class DeleteKubernetesNamespaceOnResourceGroupDeletedConsumer(
    IKubernetesClientProvider kubernetesClientProvider
    ) : IConsumer<ResourceGroupDeletedEvent>
{
    public async Task Consume(ConsumeContext<ResourceGroupDeletedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;
        var kubernetes = await kubernetesClientProvider.GetDefaultAsync(ct);

        await kubernetes.CoreV1.DeleteNamespaceAsync(
            name: message.KubernetesNamespace,
            cancellationToken: ct);
    }
}
