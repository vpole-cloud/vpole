using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Application.Persistence.Reads;
using Pole.Features.Functions.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.Functions.Application.Endpoints;

public static class DeleteFunctionApp
{
    public static IEndpointRouteBuilder MapDeleteFunctionAppEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] DeleteFunctionAppRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    DeleteFunctionAppResponse response => Results.Accepted(value: response),
                    DeleteFunctionAppError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("DeleteFunctionApp")
            .Produces<DeleteFunctionAppResponse>(StatusCodes.Status202Accepted);
        return app;
    }
}

public sealed record DeleteFunctionAppRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<DeleteFunctionAppOutcome>, ITenantWriteOperation;

public sealed record DeleteFunctionAppResponse(string FunctionAppId);

public union DeleteFunctionAppError(
    FunctionAppNotFound,
    BeginFunctionAppDeletionError);

public union DeleteFunctionAppOutcome(
    DeleteFunctionAppResponse,
    DeleteFunctionAppError);

public sealed class DeleteFunctionAppHandler(
    LoadFunctionAppById loadFunctionAppById,
    BeginFunctionAppDeletion beginFunctionAppDeletion,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<DeleteFunctionAppRequest, DeleteFunctionAppOutcome>
{
    public async Task<DeleteFunctionAppOutcome> HandleAsync(
        DeleteFunctionAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var app = await loadFunctionAppById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (app is null)
            return new DeleteFunctionAppError(new FunctionAppNotFound(request.Id));
        if (app.DesiredState == FunctionAppDesiredState.Deleting)
            return new DeleteFunctionAppResponse(app.Id);

        var deleted = await beginFunctionAppDeletion(
            app,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return deleted.Match<FunctionApp, BeginFunctionAppDeletionError, DeleteFunctionAppOutcome>(
            value => new DeleteFunctionAppResponse(value.Id),
            error => new DeleteFunctionAppError(error));
    }
}

public sealed class DeleteFunctionAppRequestValidator
    : AbstractValidator<DeleteFunctionAppRequest>
{
    public DeleteFunctionAppRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Function App ID");
}
