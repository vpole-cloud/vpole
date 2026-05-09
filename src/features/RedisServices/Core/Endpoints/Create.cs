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
using Pole.Features.Core.Kubernetes;
using Pole.Features.RedisServices.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Events;
using Pole.Features.RedisServices.Kubernetes;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Endpoints;

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
    IRedisDbContext dbContext)
    : IMediatorHandler<CreateRedisServiceRequest, CreateRedisServiceResponse>
{
    public async Task<Result<CreateRedisServiceResponse>> HandleAsync(CreateRedisServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var group = await dbContext.ResourceGroups
            .Find(rg => rg.Id == request.Body.ResourceGroupId)
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null) return Result.NotFound($"Resource group with ID '{request.Body.ResourceGroupId}' not found.");

        var newRedis = RedisFactory.Create(
            name: request.Body.Name,
            resourceGroupId: request.Body.ResourceGroupId,
            spec: request.Body.Spec.ToDomain(group.KubernetesNamespace),
            options: RedisFactory.DefaultCreateOptions with
            {
                Tags = request.Body.Tags
            }
        );
        
        return await dbContext.RunInTransactionAsync<CreateRedisServiceResponse>(async (sh, ct) =>
        {
            await dbContext.RedisServices.InsertOneAsync(sh, newRedis, cancellationToken: ct);
            
            await dbContext.StoreEventAsync(newRedis.ToCreateEvent(), sh, ct);

            return new CreateRedisServiceResponse(newRedis.Id);
        }, cancellationToken: cancellationToken);
    }
}

public class CreateRedisServiceEventConsumer(
    IKubernetes kubernetes)
    : IConsumer<CreateEvent>
{
    public async Task Consume(ConsumeContext<CreateEvent> context)
    {
        var ct = context.CancellationToken;
        var redis = context.Message.ToRedis();
        var inKuber = redis.ToKubernetes();

        var statefulSet = inKuber.ToStatefulSet();
        var service = inKuber.ToService();

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.AppsV1.CreateNamespacedStatefulSetAsync(statefulSet, redis.Spec.KubernetesNamespace, cancellationToken: ct));

        await KubernetesExtensions.CreateIfNotExistAsync(() =>
            kubernetes.CoreV1.CreateNamespacedServiceAsync(service, redis.Spec.KubernetesNamespace, cancellationToken: ct));
    }
}

public sealed record CreateRedisServiceRequest(
    [FromBody] CreateRedisServiceRequestBody Body
) : IMediatorRequest<CreateRedisServiceResponse>;

public sealed record CreateRedisServiceRequestBody(
    string Name,
    string ResourceGroupId,
    RedisSpecDto Spec,
    ImmutableHashSet<string> Tags
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
