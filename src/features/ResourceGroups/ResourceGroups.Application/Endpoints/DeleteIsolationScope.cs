using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
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
                    IHttpProblemMapper httpProblems,
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
                        IsolationScopeDeletionError error => httpProblems.Map(error).ToHttpResult()
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
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<BeginIsolationScopeDeletionOutcome>, ITenantWriteOperation;

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

public sealed class DeleteIsolationScopeHandler(
    BeginIsolationScopeDeletion beginIsolationScopeDeletion)
    : IMediatorOutcomeHandler<DeleteIsolationScopeRequest, BeginIsolationScopeDeletionOutcome>
{
    public Task<BeginIsolationScopeDeletionOutcome> HandleAsync(
        DeleteIsolationScopeRequest request,
        CancellationToken cancellationToken = default) =>
        beginIsolationScopeDeletion(
            request.TenantId,
            request.ResourceGroupId,
            request.ScopeId,
            cancellationToken);
}
