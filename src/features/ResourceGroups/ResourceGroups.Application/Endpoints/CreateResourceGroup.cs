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
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class CreateResourceGroup
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
    [FromBody] CreateResourceGroupRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<ResourceGroupDto>, ITenantCreateOperation;

public sealed record CreateResourceGroupRequestBody(
    string Name,
    string Description,
    ImmutableHashSet<string> Tags
    );

public sealed class CreateResourceGroupHandler(
    IKubernetesNamespaceGenerator namespaceGenerator,
    IResourceGroupDbContext dbContext
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
            tenantId: request.TenantId,
            name: request.Body.Name,
            description: request.Body.Description,
            kubernetesNamespace: namespaceGenerator.GenerateNamespaceName(request.TenantId, createOptions.Id),
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

public class ProvisionResourceGroupOnResourceGroupCreatedConsumer(
    IKubernetes kubernetes
    ) : IConsumer<ResourceGroupCreatedEvent>
{
    public async Task Consume(ConsumeContext<ResourceGroupCreatedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        await kubernetes.CoreV1.CreateNamespaceAsync(new V1Namespace()
        {
            ApiVersion = "v1",
            Kind = "Namespace",
            Metadata = new V1ObjectMeta()
            {
                Name = message.KubernetesNamespace,
                Labels = new Dictionary<string, string>
                {
                    ["pole.io/tenant-id"] = message.EventCore.TenantId,
                    ["pole.io/resource-group-id"] = message.ResourceGroupId
                }
            },
        }, cancellationToken: ct);
    }
}

