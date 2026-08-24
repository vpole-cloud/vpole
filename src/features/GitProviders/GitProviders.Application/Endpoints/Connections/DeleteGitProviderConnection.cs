using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record DeleteGitProviderConnectionRequest(
    [FromRoute] string ConnectionId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<DeleteGitProviderConnectionOutcome>, ITenantWriteOperation;

public union DeleteGitProviderConnectionOutcome(
    string,
    DeleteGitProviderConnectionError);

public sealed class DeleteGitProviderConnectionRequestValidator
    : AbstractValidator<DeleteGitProviderConnectionRequest>
{
    public DeleteGitProviderConnectionRequestValidator() =>
        RuleFor(request => request.ConnectionId).MongoObjectId("Git provider connection ID");
}

public sealed class DeleteGitProviderConnectionHandler(
    DeleteGitProviderConnection deleteGitProviderConnection)
    : IMediatorOutcomeHandler<DeleteGitProviderConnectionRequest, DeleteGitProviderConnectionOutcome>
{
    public async Task<DeleteGitProviderConnectionOutcome> HandleAsync(
        DeleteGitProviderConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var deleted = await deleteGitProviderConnection(
            request.TenantId,
            request.ConnectionId,
            cancellationToken);
        return deleted switch
        {
            Ok<Unit> => request.ConnectionId,
            Err<DeleteGitProviderConnectionError> error => error.Value
        };
    }
}

public static class DeleteGitProviderConnectionEndpoint
{
    public static IEndpointRouteBuilder MapDeleteGitProviderConnectionEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{ConnectionId}", async (IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] DeleteGitProviderConnectionRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    string _ => Results.NoContent(),
                    DeleteGitProviderConnectionError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("DeleteGitProviderConnection")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
