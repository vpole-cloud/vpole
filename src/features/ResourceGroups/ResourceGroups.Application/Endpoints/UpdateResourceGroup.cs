using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class UpdateResourceGroup
{
    public static IEndpointRouteBuilder MapUpdateResourceGroupEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{Id}", async (IMediator mediator, [AsParameters] UpdateResourceGroupRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateResourceGroup")
            .WithSummary("Updates a resource group by its ID.")
            .WithDescription("Updates the resource group's metadata while preserving its Kubernetes namespace.")
            .Produces<ResourceGroupDto>(StatusCodes.Status200OK);

        return app;
    }
}

public sealed record UpdateResourceGroupRequest(
    [FromRoute] string Id,
    [FromBody] UpdateResourceGroupRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null
) : IMediatorRequest<ResourceGroupDto>, ITenantWriteOperation;

public sealed record UpdateResourceGroupRequestBody(
    string Description,
    ImmutableHashSet<string> Tags
);

public sealed class UpdateResourceGroupHandler(
    IResourceGroupDbContext dbContext)
    : IMediatorHandler<UpdateResourceGroupRequest, ResourceGroupDto>
{
    public async Task<Result<ResourceGroupDto>> HandleAsync(
        UpdateResourceGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ResourceGroup>.Filter.And(
            Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
            Builders<ResourceGroup>.Filter.Eq(rg => rg.Id, request.Id));

        var resourceGroup = await dbContext.ResourceGroups
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken);

        if (resourceGroup is null)
            return Failures.NotFound("Resource group not found.", request.Id);

        return await dbContext.RunInTransactionAsync<ResourceGroupDto>(async (sh, ct) =>
        {
            var changes = GetChanges(resourceGroup, request.Body);
            if (HasChanges(changes))
            {
                await dbContext.ResourceGroups.UpdateOneAsync(
                    session: sh,
                    filter: filter,
                    update: GetUpdateDefinition(changes),
                    cancellationToken: ct);
            }

            await dbContext.StoreEventAsync(new ResourceGroupUpdatedEvent(
                request.Id,
                resourceGroup.Name,
                request.Body.Description,
                resourceGroup.KubernetesNamespace,
                resourceGroup.Resources,
                changes)
            {
                EventCore = EventCore.Create(resourceGroup.TenantId)
            }, sh, ct);

            return new ResourceGroupDto(
                Id: resourceGroup.Id,
                Name: resourceGroup.Name,
                Description: request.Body.Description,
                Resources: resourceGroup.Resources.Select(x => new ShortResourceInfoDto(x.Id, x.Name, x.Type)),
                Tags: request.Body.Tags
            );
        }, cancellationToken: cancellationToken);
    }

    private static ResourceGroupUpdatedEventChanges GetChanges(ResourceGroup resourceGroup, UpdateResourceGroupRequestBody body) =>
        new(UpdateLog<string>.Of(resourceGroup.Description, body.Description),
            UpdateLog<ImmutableHashSet<string>>.Of(
                resourceGroup.Tags,
                body.Tags));

    private static UpdateDefinition<ResourceGroup> GetUpdateDefinition(ResourceGroupUpdatedEventChanges changes)
    {
        var updates = new Queue<UpdateDefinition<ResourceGroup>>();
        if (!changes.Description.AreEqual)
            updates.Enqueue(Builders<ResourceGroup>.Update.Set(rg => rg.Description, changes.Description.New));
        if (!changes.Tags.AreEqual)
            updates.Enqueue(Builders<ResourceGroup>.Update.Set(rg => rg.Tags, changes.Tags.New));
        return Builders<ResourceGroup>.Update.Combine(updates);
    }

    private static bool HasChanges(ResourceGroupUpdatedEventChanges changes) =>
        !changes.Description.AreEqual || !changes.Tags.AreEqual;
}

public sealed class UpdateResourceGroupValidator : AbstractValidator<UpdateResourceGroupRequest>
{
    public UpdateResourceGroupValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty();
        RuleFor(x => x.Body.Description)
            .MaximumLength(200);
        RuleForEach(x => x.Body.Tags)
            .NotEmpty()
            .MaximumLength(30);
    }
}

