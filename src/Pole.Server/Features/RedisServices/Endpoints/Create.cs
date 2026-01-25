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

public static class Create
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapCreateRedisServiceEndpoint()
        {
            app.MapPost("/", async ([FromServices] IMediator mediator, [AsParameters] CreateRedisServiceRequest request) =>
                {
                    var response = await mediator.Send(request);
                    return response.ToHttpResult(v => Results.Accepted(value: v));
                })
                .WithName("CreateRedisService")
                .Produces<CreateRedisServiceResponse>(StatusCodes.Status202Accepted);

            return app;
        }
    }
}

public sealed class CreateRedisServiceHandler(
    MongoDbContext dbContext)
    : IMediatorHandler<CreateRedisServiceRequest, CreateRedisServiceResponse>
{
    public async Task<Result<CreateRedisServiceResponse>> HandleAsync(CreateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var group = await dbContext.ResourceGroups
            .Find(rg => rg.Id == request.Body.ResourceGroupId)
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null) return Result.NotFound($"Resource group with ID '{request.Body.ResourceGroupId}' not found.");

        var newRedis = new Redis(
            Name: request.Body.Name,
            ResourceGroupId: request.Body.ResourceGroupId,
            Namespace: group.NamespaceName,
            Spec: request.Body.Spec.ToDomain()
        );
        
        return await dbContext.RunInTransactionAsync<CreateRedisServiceResponse>(async (sh, ct) =>
        {
            await dbContext.RedisServices.InsertOneAsync(sh, newRedis, cancellationToken: ct);
            
            await dbContext.StoreEventAsync(new CreateEvent(newRedis.Id), sh, ct);

            return new CreateRedisServiceResponse(newRedis.Id);
        }, cancellationToken: cancellationToken);
    }
}

public class CreateRedisServiceEventConsumer(
    MongoDbContext dbContext,
    IKubernetes kubernetes)
    : IConsumer<CreateEvent>
{
    public async Task Consume(ConsumeContext<CreateEvent> context)
    {
        var ct = context.CancellationToken;

        var redis = await dbContext.RedisServices
            .Find(r => r.Id == context.Message.RedisId)
            .FirstOrDefaultAsync(ct);
        if (redis is null) return;
        var inKuber = redis.ToKubernetes();

        var statefulSet = inKuber.ToStatefulSet();
        var service = inKuber.ToService();

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.AppsV1.CreateNamespacedStatefulSetAsync(statefulSet, redis.Namespace, cancellationToken: ct));

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.CoreV1.CreateNamespacedServiceAsync(service, redis.Namespace, cancellationToken: ct));
    }
}

public sealed record CreateRedisServiceRequest(
    [FromBody] CreateRedisServiceRequestBody Body
) : IMediatorRequest<CreateRedisServiceResponse>;

public sealed record CreateRedisServiceRequestBody(
    string Name,
    string ResourceGroupId,
    RedisSpecDto Spec
);

public sealed record CreateRedisServiceResponse(
    string RedisId
);

public sealed class CreateRedisServiceRequestValidator : AbstractValidator<CreateRedisServiceRequest>
{
    public CreateRedisServiceRequestValidator()
    {
        RuleFor(x => x.Body).SetValidator(new CreateRedisServiceRequestBodyValidator());
    }
}

public sealed class CreateRedisServiceRequestBodyValidator : AbstractValidator<CreateRedisServiceRequestBody>
{
    public CreateRedisServiceRequestBodyValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(30)
            .Matches("^[a-z0-9-]+$")
            .WithMessage("Name must contain only lowercase letters, numbers, and hyphens.");
        RuleFor(x => x.ResourceGroupId)
            .NotEmpty();
        RuleFor(x => x.Spec)
            .SetValidator(new RedisSpecDtoValidator());
    }
}

