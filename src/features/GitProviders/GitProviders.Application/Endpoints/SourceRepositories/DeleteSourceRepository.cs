using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence.Repositories;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.GitProviders.Application.Endpoints.SourceRepositories;

public sealed record DeleteSourceRepositoryRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<DeleteSourceRepositoryOutcome>, ITenantWriteOperation;

public union DeleteSourceRepositoryError(
    SourceRepositoryNotFound,
    SourceRepositoryHasBuildConfiguration);

public union DeleteSourceRepositoryOutcome(
    string,
    DeleteSourceRepositoryError);

public sealed class DeleteSourceRepositoryRequestValidator
    : AbstractValidator<DeleteSourceRepositoryRequest>
{
    public DeleteSourceRepositoryRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Source repository ID");
}

public sealed class DeleteSourceRepositoryHandler(
    EnsureNoSourceRepositoryBuildConfiguration ensureNoSourceRepositoryBuildConfiguration,
    DeleteSourceRepository deleteSourceRepository)
    : IMediatorOutcomeHandler<DeleteSourceRepositoryRequest, DeleteSourceRepositoryOutcome>
{
    public async Task<DeleteSourceRepositoryOutcome> HandleAsync(
        DeleteSourceRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var configurationGuard = await ensureNoSourceRepositoryBuildConfiguration(
            request.TenantId,
            request.Id,
            cancellationToken);
        return configurationGuard switch
        {
            Err<SourceBuildSourceRepositoryConfigurationExists> =>
                new DeleteSourceRepositoryError(
                    new SourceRepositoryHasBuildConfiguration(request.Id)),
            Ok<Unit> => await DeleteAsync(
                request,
                deleteSourceRepository,
                cancellationToken)
        };
    }

    private static async Task<DeleteSourceRepositoryOutcome> DeleteAsync(
        DeleteSourceRepositoryRequest request,
        DeleteSourceRepository deleteSourceRepository,
        CancellationToken cancellationToken)
    {
        var deleted = await deleteSourceRepository(request.TenantId, request.Id, cancellationToken);
        return deleted switch
        {
            Ok<Unit> => request.Id,
            Err<SourceRepositoryNotFound> error =>
                new DeleteSourceRepositoryError(error.Value)
        };
    }
}

public static class DeleteSourceRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapDeleteSourceRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] DeleteSourceRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    string _ => Results.NoContent(),
                    DeleteSourceRepositoryError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("DeleteSourceRepository")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
