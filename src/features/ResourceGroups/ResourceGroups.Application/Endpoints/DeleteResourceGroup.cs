// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.ResourceGroups;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class DeleteResourceGroup
{
    public static IEndpointRouteBuilder MapDeleteResourceGroupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete(
                "/{Id}",
                async (
                    IMediator mediator, [AsParameters] DeleteResourceGroupRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        ResourceGroupDeletionCompleted => Results.NoContent(),
                        ResourceGroupDeletionRequested => Results.Accepted(
                            value: new DeleteResourceGroupResponse(Deleted: false, DeletionRequested: true)),
                        ResourceGroupDeletionError error => ResourceGroupHttpMappings.Map(error),
                    });
                })
            .WithName("DeleteResourceGroup")
            .WithSummary("Deletes a resource group by its ID.")
            .WithDescription("Deletes an empty resource group immediately, or starts deletion of its runtime cells.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<DeleteResourceGroupResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}

public sealed record DeleteResourceGroupRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId
) : IMediatorRequest<DeleteResourceGroupOutcome>, ITenantWriteOperation;

public sealed class DeleteResourceGroupRequestValidator : AbstractValidator<DeleteResourceGroupRequest>
{
    public DeleteResourceGroupRequestValidator() => RuleFor(request => request.Id).MongoObjectId("Resource group ID");
}

public sealed record DeleteResourceGroupResponse(
    bool Deleted,
    bool DeletionRequested
);

public union DeleteResourceGroupOutcome(
    ResourceGroupDeletionCompleted,
    ResourceGroupDeletionRequested,
    ResourceGroupDeletionError);

public sealed class DeleteResourceGroupHandler(
    BeginResourceGroupDeletion beginResourceGroupDeletion
) : IMediatorOutcomeHandler<DeleteResourceGroupRequest, DeleteResourceGroupOutcome>
{
    public async Task<DeleteResourceGroupOutcome> HandleAsync(
        DeleteResourceGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        var outcome = await beginResourceGroupDeletion(request.TenantId, request.Id, cancellationToken);
        return outcome switch
        {
            ResourceGroupDeletionCompleted completed => new DeleteResourceGroupOutcome(completed),
            ResourceGroupDeletionRequested requested => new DeleteResourceGroupOutcome(requested),
            ResourceGroupDeletionError error => new DeleteResourceGroupOutcome(error),
        };
    }
}
