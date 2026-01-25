using FluentValidation;
using k8s;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Database;
using Pole.Server.Extensions;
using Pole.Server.Features.RedisServices.Common;
using Pole.Server.Features.RedisServices.Events;
using Pole.Server.Kuber;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Features.RedisServices.Endpoints;

public static class Update
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapUpdateRedisServiceEndpoint()
        {
            app.MapPut("/{Id}", async (IMediator mediator, [AsParameters] UpdateRedisServiceRequest request, CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(x => Results.Accepted(value: x));
                })
                .WithName("UpdateRedisService")
                .Produces<UpdateRedisServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed record UpdateRedisServiceRequest(
    [FromRoute] string Id,
    [FromBody] UpdateRedisServiceRequestBody Body
) : IMediatorRequest<UpdateRedisServiceResponse>;

public sealed record UpdateRedisServiceRequestBody(
    string ResourceGroupId,
    RedisSpecDto Spec
);

public sealed record UpdateRedisServiceResponse(string Id);

public sealed class UpdateRedisServiceHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<UpdateRedisServiceRequest, UpdateRedisServiceResponse>
{
    public async Task<Result<UpdateRedisServiceResponse>> HandleAsync(
        UpdateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var redis = await dbContext.RedisServices
            .Find(r => r.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (redis is null)
            return Result.NotFound($"Redis service with ID '{request.Id}' not found.");

        return await dbContext.RunInTransactionAsync<UpdateRedisServiceResponse>(async (sh, ct) =>
        {
            await dbContext.RedisServices.UpdateOneAsync(
                session: sh,
                r => r.Id == request.Id,
                Builders<Redis>.Update
                    .Set(r => r.Spec, request.Body.Spec.ToDomain()),
                cancellationToken: ct);

            await dbContext.StoreEventAsync(new UpdateEvent
            {
                RedisId = request.Id
            }, sh, ct);
            
            return new UpdateRedisServiceResponse(
                Id: redis.Id
            );
        }, cancellationToken: cancellationToken);
    }
}

public class UpdateRedisServiceEventConsumer(
    MongoDbContext dbContext,
    IKubernetes kubernetes)
    : IConsumer<UpdateEvent>
{
    public async Task Consume(ConsumeContext<UpdateEvent> context)
    {
        var ct = context.CancellationToken;
        var redis = await dbContext.RedisServices
            .Find(r => r.Id == context.Message.RedisId)
            .FirstOrDefaultAsync(ct);
        if (redis is null) return;

        var resourceGroup = await dbContext.ResourceGroups
            .Find(rg => rg.Id == redis.ResourceGroupId)
            .FirstOrDefaultAsync(ct);
        if (resourceGroup is null) return;

        var inKuber = redis.ToKubernetes();

        var statefulSet = inKuber.ToStatefulSet();
        var service = inKuber.ToService();
        
        await kubernetes.AppsV1.ReplaceNamespacedStatefulSetAsync(statefulSet, statefulSet.Metadata.Name, redis.Namespace, cancellationToken: ct);
        await kubernetes.CoreV1.ReplaceNamespacedServiceAsync(service, service.Metadata.Name, redis.Namespace, cancellationToken: ct);
    }
}

public sealed class UpdateRedisServiceValidator : AbstractValidator<UpdateRedisServiceRequest>
{
    public UpdateRedisServiceValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithMessage("Redis service ID is required.");

        RuleFor(x => x.Body.ResourceGroupId)
            .NotEmpty().WithMessage("Resource group ID is required.");

        RuleFor(x => x.Body.Spec)
            .NotNull().WithMessage("Redis service specification is required.")
            .SetValidator(new RedisSpecDtoValidator());
    }
}
