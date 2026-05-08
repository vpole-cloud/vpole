using System.Collections.Immutable;
using FluentValidation;
using k8s;
using k8s.Models;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Domain.Models;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.ResourceGroups.Services;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Endpoints;

public static class Create
{
    public static IEndpointRouteBuilder MapCreateResourceGroupEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async ([AsParameters] CreateResourceGroupRequest request, [FromServices] IMediator mediator) =>
            {
                var result = await mediator.Send(request);
                return result.ToHttpResult(r => Results.Created($"/resource-groups/{r.Id}", r));
            })
            .WithName("CreateResourceGroup")
            .WithSummary("Creates a new resource group.")
            .WithDescription("Creates a new resource group with the specified details.")
            .Produces<ResourceGroupDto>(StatusCodes.Status201Created);

        return app;
    }
    
}

public sealed record CreateResourceGroupRequest(
    [FromBody] CreateResourceGroupRequestBody Body
) : IMediatorRequest<ResourceGroupDto>;

public sealed record CreateResourceGroupRequestBody(
    string Name,
    string Description,
    ImmutableHashSet<string> Tags
    );

public sealed class CreateResourceGroupHandler(
    IKubernetesNamespaceGenerator namespaceGenerator,
    MongoDbContext dbContext
    ) : IMediatorHandler<CreateResourceGroupRequest, ResourceGroupDto>
{
    public async Task<Result<ResourceGroupDto>> HandleAsync(
        CreateResourceGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var createOptions = ResourceGroupFactory.DefaultCreateOptions with
        {
            Tags = request.Body.Tags
        };
        var resourceGroup = ResourceGroupFactory.Create(
            name: request.Body.Name,
            description: request.Body.Description,
            kubernetesNamespace: namespaceGenerator.GenerateNamespaceName(createOptions.Id),
            options: createOptions
        );
        return await dbContext.RunInTransactionAsync<ResourceGroupDto>(async (sh, ct) =>
        {
            await dbContext.ResourceGroups.InsertOneAsync(
                session: sh,
                document: resourceGroup,
                cancellationToken: ct);
            
            await dbContext.StoreEventAsync(resourceGroup.ToCreateEvent(), sh, ct);
            
            return resourceGroup.ToDto();
        }, cancellationToken: cancellationToken);
    }
}

public sealed class CreateResourceGroupValidator : AbstractValidator<CreateResourceGroupRequest>
{
    public CreateResourceGroupValidator()
    {
        RuleFor(x => x.Body.Name)
            .NotEmpty()
            .MaximumLength(20);
        RuleFor(x => x.Body.Description)
            .MaximumLength(200);
        RuleForEach(x => x.Body.Tags)
            .NotEmpty()
            .MaximumLength(30);
    }
}

public class CreateResourceGroupEventConsumer(
    IKubernetes kubernetes
    ) : IConsumer<CreateEvent>
{
    public async Task Consume(ConsumeContext<CreateEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        await kubernetes.CoreV1.CreateNamespaceAsync(new V1Namespace()
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta()
            {
                Name = message.KubernetesNamespace
            },
        }, cancellationToken: ct);
    }
}
