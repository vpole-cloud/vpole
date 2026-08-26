using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class DeleteResourceGroup
{
    public static IEndpointRouteBuilder MapDeleteResourceGroupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] DeleteResourceGroupRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ResourceGroupDeletionCompleted => Results.NoContent(),
                    ResourceGroupDeletionRequested => Results.Accepted(value:
                        new DeleteResourceGroupResponse(
                            Deleted: false,
                            DeletionRequested: true)),
                    ResourceGroupDeletionError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("DeleteResourceGroup")
            .WithSummary("Deletes a resource group by its ID.")
            .WithDescription(
                "Deletes an empty resource group immediately, or starts deletion of its runtime cells.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<DeleteResourceGroupResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}

public sealed record DeleteResourceGroupRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<BeginResourceGroupDeletionOutcome>, ITenantWriteOperation;

public sealed class DeleteResourceGroupRequestValidator
    : AbstractValidator<DeleteResourceGroupRequest>
{
    public DeleteResourceGroupRequestValidator() =>
        RuleFor(request => request.Id)
            .MongoObjectId("Resource group ID");
}

public sealed record DeleteResourceGroupResponse(
    bool Deleted,
    bool DeletionRequested);

public sealed class DeleteResourceGroupHandler(
    BeginResourceGroupDeletion beginResourceGroupDeletion)
    : IMediatorOutcomeHandler<DeleteResourceGroupRequest, BeginResourceGroupDeletionOutcome>
{
    public Task<BeginResourceGroupDeletionOutcome> HandleAsync(
        DeleteResourceGroupRequest request,
        CancellationToken cancellationToken = default) =>
        beginResourceGroupDeletion(request.TenantId, request.Id, cancellationToken);
}
