using System.Collections.Immutable;
using FluentValidation;
using k8s;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Core.Multitenancy;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Events;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Endpoints;

public static class UpdateRedisService
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
    [FromBody] UpdateRedisServiceRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null
) : IMediatorRequest<UpdateRedisServiceResponse>, ITenantWriteOperation;

public sealed record UpdateRedisServiceRequestBody(
    string ResourceGroupId,
    RedisSpecDto Spec,
    ImmutableHashSet<string> Tags
);

public sealed record UpdateRedisServiceResponse(string Id);

public sealed class UpdateRedisServiceHandler(
    IRedisDbContext dbContext)
    : IMediatorHandler<UpdateRedisServiceRequest, UpdateRedisServiceResponse>
{
    public async Task<Result<UpdateRedisServiceResponse>> HandleAsync(
        UpdateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<Redis>.Filter.And(
            Builders<Redis>.Filter.ForTenant(request.TenantId),
            Builders<Redis>.Filter.Eq(r => r.Id, request.Id));

        var redis = await dbContext.RedisServices
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (redis is null)
            return Result.NotFound($"Redis service with ID '{request.Id}' not found.");

        return await dbContext.RunInTransactionAsync<UpdateRedisServiceResponse>(async (sh, ct) =>
        {
            var changes = GetChanges(redis, request.Body);
            if (HasChanges(changes))
            {
                await dbContext.RedisServices.UpdateOneAsync(
                    session: sh,
                    filter,
                    GetUpdateDefinition(changes),
                    cancellationToken: ct);
            }

            await dbContext.StoreEventAsync(new RedisServiceUpdatedEvent(
                    request.Id,
                    redis.Name,
                    redis.ResourceGroupId,
                    changes)
                {
                    EventCore = EventCore.Create(redis.TenantId)
                }
                , sh, ct);
            
            return new UpdateRedisServiceResponse(
                Id: redis.Id
            );
        }, cancellationToken: cancellationToken);
    }

    private static RedisServiceUpdatedEventChanges GetChanges(Redis redis, UpdateRedisServiceRequestBody body) =>
        new(UpdateLog<RedisServiceSpecSnapshot>.Of(
                redis.Spec.ToSnapshot(),
                body.Spec.ToDomain(redis.Spec.KubernetesNamespace).ToSnapshot()),
            UpdateLog<ImmutableHashSet<string>>.Of(
                redis.Tags,
                body.Tags));

    private static UpdateDefinition<Redis> GetUpdateDefinition(RedisServiceUpdatedEventChanges changes)
    {
        var updates = new Queue<UpdateDefinition<Redis>>();
        if (!changes.RedisSpec.AreEqual)
            updates.Enqueue(Builders<Redis>.Update.Set(redis => redis.Spec, changes.RedisSpec.New.ToDomain()));
        if (!changes.Tags.AreEqual)
            updates.Enqueue(Builders<Redis>.Update.Set(redis => redis.Tags, changes.Tags.New));
        return Builders<Redis>.Update.Combine(updates);
    }

    private static bool HasChanges(RedisServiceUpdatedEventChanges changes) =>
        !changes.RedisSpec.AreEqual || !changes.Tags.AreEqual;
}

public class UpdateRedisServiceEventConsumer(
    IRedisDbContext dbContext,
    IKubernetes kubernetes)
    : IConsumer<RedisServiceUpdatedEvent>
{
    public async Task Consume(ConsumeContext<RedisServiceUpdatedEvent> context)
    {
        var ct = context.CancellationToken;
        var redis = await dbContext.RedisServices
            .Find(r => r.Id == context.Message.RedisId && r.TenantId == context.Message.EventCore.TenantId)
            .FirstOrDefaultAsync(ct);
        if (redis is null) return;

        var inKuber = redis.ToKubernetes();

        await RedisKubernetesResourceManager.ReplaceAsync(kubernetes, inKuber, ct);
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

