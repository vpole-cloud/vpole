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
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<ResourceGroupDto>, ITenantWriteOperation;

public sealed record UpdateResourceGroupRequestBody(
    string Description,
    ImmutableHashSet<string> Tags
);

public sealed class UpdateResourceGroupHandler(
    IResourceGroupDbContext dbContext,
    TimeProvider timeProvider)
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

        var updated = resourceGroup.UpdateMetadata(
            request.Body.Description,
            request.Body.Tags,
            timeProvider.GetUtcNow().UtcDateTime);
        var changes = GetChanges(resourceGroup, updated);

        return await dbContext.RunInTransactionAsync<ResourceGroupDto>(async (sh, ct) =>
        {
            if (updated.Revision != resourceGroup.Revision)
            {
                var replacement = await dbContext.ResourceGroups.ReplaceOneAsync(
                    sh,
                    Builders<ResourceGroup>.Filter.And(
                        filter,
                        Builders<ResourceGroup>.Filter.Eq(
                            group => group.Revision,
                            resourceGroup.Revision)),
                    updated,
                    cancellationToken: ct);
                if (replacement.ModifiedCount != 1)
                    return Failures.Conflict("The resource group changed while it was being updated.");
            }

            await dbContext.StoreEventAsync(new ResourceGroupUpdatedEvent(
                request.Id,
                updated.Name,
                updated.Description,
                updated.KubernetesNamespace,
                updated.Resources,
                changes)
            {
                EventCore = EventCore.Create(updated.TenantId)
            }, sh, ct);

            return updated.ToDto();
        }, cancellationToken: cancellationToken);
    }

    private static ResourceGroupUpdatedEventChanges GetChanges(ResourceGroup previous, ResourceGroup updated) =>
        new(UpdateLog<string>.Of(previous.Description, updated.Description),
            UpdateLog<ImmutableHashSet<string>>.Of(
                previous.Tags,
                updated.Tags));
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
