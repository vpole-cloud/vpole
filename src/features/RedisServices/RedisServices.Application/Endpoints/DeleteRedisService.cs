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
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Events;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.Quotas.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class DeleteRedisService
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
    IRedisDbContext dbContext,
    IQuotaAdmissionExecutor quotaAdmissionExecutor
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
            return Failures.NotFound("Redis service not found.", request.Id);
        
        return await quotaAdmissionExecutor.ExecuteAsync<RedisSpec, DeleteRedisServiceResponse>(
            redis.TenantId,
            RedisQuotaResourceKey.Create(redis.Id),
            new DeleteQuotaMutation<RedisSpec>(redis.Spec),
            async (session, transactionCancellationToken) =>
            {
                var deleteResult = await dbContext.RedisServices.DeleteOneAsync(
                    session,
                    filter,
                    cancellationToken: transactionCancellationToken);
                if (deleteResult.DeletedCount == 0)
                    return Failures.NotFound("Redis service not found.", request.Id);

                await dbContext.StoreEventAsync(
                    redis.ToDeleteEvent(),
                    session,
                    transactionCancellationToken);

                return new DeleteRedisServiceResponse(request.Id);
            },
            cancellationToken);
    }
}

public class DeleteRedisServiceFromKubernetesOnRedisServiceDeletedConsumer(
    IKubernetes kubernetes
    ) : IConsumer<RedisServiceDeletedEvent>
{
    public async Task Consume(ConsumeContext<RedisServiceDeletedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        await RedisKubernetesResourceManager.DeleteAsync(kubernetes, message.Name, message.Spec.KubernetesNamespace, ct);
    }
}

public sealed record DeleteRedisServiceRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<DeleteRedisServiceResponse>, ITenantWriteOperation;

public sealed record DeleteRedisServiceResponse(
    string Id
);

