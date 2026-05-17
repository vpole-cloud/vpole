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
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Common;
using Pole.Features.RedisServices.Events;
using Pole.Features.RedisServices.Kubernetes;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Endpoints;

public static class Delete
{
    public static IEndpointRouteBuilder MapDeleteRedisServiceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (IMediator mediator, [AsParameters] DeleteRedisServiceRequest request, CancellationToken ct) =>
            {
                var result = await mediator.Send(request, ct);
                return result.ToHttpResult(x => Results.Accepted(value: x.Id));
            })
            .WithName("DeleteRedisService")
            .Produces(StatusCodes.Status202Accepted);

        return app;
    }
}

public sealed class DeleteRedisServiceHandler(
    IRedisDbContext dbContext
    ) : IMediatorHandler<DeleteRedisServiceRequest, DeleteRedisServiceResponse>
{
    public async Task<Result<DeleteRedisServiceResponse>> HandleAsync(DeleteRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<Redis>.Filter.And(
            Builders<Redis>.Filter.ForTenant(request.TenantId),
            Builders<Redis>.Filter.Eq(r => r.Id, request.Id));

        var redis = await dbContext.RedisServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken);
        if (redis is null)
            return Result.NotFound($"Redis service with ID '{request.Id}' not found.");
        
        return await dbContext.RunInTransactionAsync<DeleteRedisServiceResponse>(async (sh, ct) =>
        {
            await dbContext.RedisServices
                .DeleteOneAsync(filter, cancellationToken: ct);
        
            await dbContext.StoreEventAsync(redis.ToDeleteEvent(), sh, ct);
            return new DeleteRedisServiceResponse(
                Id: request.Id
            );
        }, cancellationToken: cancellationToken);
    }
}

public class DeleteRedisServiceRequestedEventConsumer(
    IKubernetes kubernetes
    ) : IConsumer<DeleteEvent>
{
    public async Task Consume(ConsumeContext<DeleteEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        await RedisKubernetesResourceManager.DeleteAsync(kubernetes, message.Name, message.Spec.KubernetesNamespace, ct);
    }
}

public sealed record DeleteRedisServiceRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null
) : IMediatorRequest<DeleteRedisServiceResponse>, ITenantWriteOperation;

public sealed record DeleteRedisServiceResponse(
    string Id
);
