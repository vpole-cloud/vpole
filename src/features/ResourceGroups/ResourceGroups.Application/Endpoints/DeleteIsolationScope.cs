using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.IsolationScopes;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public static class DeleteIsolationScope
{
    public static IEndpointRouteBuilder MapDeleteIsolationScopeEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapDelete(
                "/{ResourceGroupId}/isolation-scopes/{ScopeId}",
                async (
                    IMediator mediator,
                    [AsParameters] DeleteIsolationScopeRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        IsolationScopeDeletionCompleted => Results.NoContent(),
                        IsolationScopeDeletionRequested requested => Results.Accepted(value:
                            new DeleteIsolationScopeResponse(
                                requested.IsolationScopeId,
                                Deleted: false,
                                DeletionRequested: true)),
                        IsolationScopeDeletionError error => ResourceGroupHttpMappings.Map(error)
                    });
                })
            .WithName("DeleteIsolationScope")
            .WithSummary("Deletes an additional isolation scope.")
            .WithDescription(
                "Deletes an empty additional scope immediately, or starts deletion of its runtime cells.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<DeleteIsolationScopeResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}

public sealed record DeleteIsolationScopeRequest(
    [FromRoute] string ResourceGroupId,
    [FromRoute] string ScopeId,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<DeleteIsolationScopeOutcome>, ITenantWriteOperation;

public sealed record DeleteIsolationScopeResponse(
    string Id,
    bool Deleted,
    bool DeletionRequested);

public sealed class DeleteIsolationScopeRequestValidator
    : AbstractValidator<DeleteIsolationScopeRequest>
{
    public DeleteIsolationScopeRequestValidator()
    {
        RuleFor(request => request.ResourceGroupId)
            .MongoObjectId("Resource group ID");
        RuleFor(request => request.ScopeId)
            .MongoObjectId("Isolation scope ID");
    }
}

public union DeleteIsolationScopeOutcome(
    IsolationScopeDeletionCompleted,
    IsolationScopeDeletionRequested,
    IsolationScopeDeletionError);

public sealed class DeleteIsolationScopeHandler(
    BeginIsolationScopeDeletion beginIsolationScopeDeletion)
    : IMediatorOutcomeHandler<DeleteIsolationScopeRequest, DeleteIsolationScopeOutcome>
{
    public async Task<DeleteIsolationScopeOutcome> HandleAsync(
        DeleteIsolationScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        var outcome = await beginIsolationScopeDeletion(
            request.TenantId,
            request.ResourceGroupId,
            request.ScopeId,
            cancellationToken);
        return outcome switch
        {
            IsolationScopeDeletionCompleted completed => new DeleteIsolationScopeOutcome(completed),
            IsolationScopeDeletionRequested requested => new DeleteIsolationScopeOutcome(requested),
            IsolationScopeDeletionError error => new DeleteIsolationScopeOutcome(error)
        };
    }
}
