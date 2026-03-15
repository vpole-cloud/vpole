using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.Features.Database;
using Pole.Features.Kuber;
using Pole.Features.RedisServices.Events;
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
    MongoDbContext dbContext
    ) : IMediatorHandler<DeleteRedisServiceRequest, DeleteRedisServiceResponse>
{
    public async Task<Result<DeleteRedisServiceResponse>> HandleAsync(DeleteRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await dbContext.RedisServices
            .Find(r => r.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (redis is null)
            return Result.NotFound($"Redis service with ID '{request.Id}' not found.");
        
        return await dbContext.RunInTransactionAsync<DeleteRedisServiceResponse>(async (sh, ct) =>
        {
            await dbContext.RedisServices
                .DeleteOneAsync(r => r.Id == request.Id, cancellationToken: ct);
        
            await dbContext.StoreEventAsync(new DeleteEvent(redis.Id, redis.Name, redis.Namespace), sh, ct);
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

        await kubernetes.AppsV1.DeleteNamespacedStatefulSetAsync(
            name: message.RedisName,
            namespaceParameter: message.Namespace,
            cancellationToken: ct);
        
        await kubernetes.CoreV1.DeleteNamespacedServiceAsync(
            name: RedisInKubernetes.BuildServiceName(message.RedisName),
            namespaceParameter: message.Namespace,
            cancellationToken: ct);
    }
}

public sealed record DeleteRedisServiceRequest(
    [FromRoute] string Id
) : IMediatorRequest<DeleteRedisServiceResponse>;

public sealed record DeleteRedisServiceResponse(
    string Id
);
