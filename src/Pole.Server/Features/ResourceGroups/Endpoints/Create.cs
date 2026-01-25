using System.Collections.Immutable;
using k8s;
using k8s.Models;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Pole.Server.Database;
using Pole.Server.Extensions;
using Pole.Server.Features.ResourceGroups.Common;
using Pole.Server.Features.ResourceGroups.Events;
using Pole.Server.Mediator;
using Pole.Server.ResultType;

namespace Pole.Server.Features.ResourceGroups.Endpoints;

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
    ImmutableList<string> Tags
    );

public sealed class CreateResourceGroupHandler(
    MongoDbContext dbContext
    ) : IMediatorHandler<CreateResourceGroupRequest, ResourceGroupDto>
{
    public async Task<Result<ResourceGroupDto>> HandleAsync(
        CreateResourceGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = new ResourceGroup(request.Body.Name, request.Body.Description, [], request.Body.Tags);
        
        return await dbContext.RunInTransactionAsync<ResourceGroupDto>(async (sh, ct) =>
        {
            await dbContext.ResourceGroups.InsertOneAsync(
                session: sh,
                document: resourceGroup,
                cancellationToken: ct);
            
            await dbContext.StoreEventAsync(new CreateEvent(
                ResourceGroupId: resourceGroup.Id,
                NamespaceName: resourceGroup.NamespaceName
            ), sh, ct);
            
            return resourceGroup.ToDto();
        }, cancellationToken: cancellationToken);
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
                Name = message.NamespaceName
            },
        }, cancellationToken: ct);
    }
}