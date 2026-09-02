using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class UpdateResourceGroup
{
    public static IEndpointRouteBuilder MapUpdateResourceGroupEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{Id}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] UpdateResourceGroupRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ResourceGroupDto resourceGroup => Results.Ok(resourceGroup),
                    UpdateResourceGroupError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("UpdateResourceGroup")
            .WithSummary("Updates a resource group by its ID.")
            .WithDescription("Updates the resource group's metadata while preserving its ownership identity.")
            .Produces<ResourceGroupDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}

public sealed record UpdateResourceGroupRequest(
    [FromRoute] string Id,
    [FromBody] UpdateResourceGroupRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId
) : IMediatorRequest<UpdateResourceGroupOutcome>, ITenantWriteOperation;

public union UpdateResourceGroupError(
    ResourceGroupNotFound,
    ResourceGroupRevisionConflict);

public union UpdateResourceGroupOutcome(ResourceGroupDto, UpdateResourceGroupError);

public sealed record UpdateResourceGroupRequestBody(
    string Description,
    ImmutableHashSet<string> Tags
);

public sealed class UpdateResourceGroupHandler(
    LoadResourceGroup loadResourceGroup,
    LoadResourceRegistrationCount loadResourceRegistrationCount,
    PersistUpdatedResourceGroup persistUpdatedResourceGroup,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<UpdateResourceGroupRequest, UpdateResourceGroupOutcome>
{
    public async Task<UpdateResourceGroupOutcome> HandleAsync(
        UpdateResourceGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await loadResourceGroup(
            request.TenantId,
            request.Id,
            cancellationToken);

        if (resourceGroup is null)
            return new UpdateResourceGroupError(new ResourceGroupNotFound(request.Id));

        var updated = resourceGroup.UpdateMetadata(
            request.Body.Description,
            request.Body.Tags,
            timeProvider.GetUtcNow().UtcDateTime);
        var changes = GetChanges(resourceGroup, updated);

        var persisted = await persistUpdatedResourceGroup(
            resourceGroup,
            updated,
            changes,
            cancellationToken);
        return persisted switch
        {
            Ok<ResourceGroup> success => success.Value.ToDto(await loadResourceRegistrationCount(
                request.TenantId,
                request.Id,
                cancellationToken)),
            Err<ResourceGroupRevisionConflict> error => new UpdateResourceGroupError(error.Value)
        };
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
            .MongoObjectId("Resource group ID");
        RuleFor(x => x.Body.Description)
            .MaximumLength(200);
        RuleForEach(x => x.Body.Tags)
            .NotEmpty()
            .MaximumLength(30);
    }
}
