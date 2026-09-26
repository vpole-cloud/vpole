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
using Pole.Validation;
using static Pole.Features.GitProviders.Application.Common.GitProviderHttpMappings;

namespace Pole.Features.GitProviders.Application.Endpoints.SourceRepositories;

public sealed record DeleteSourceRepositoryRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<DeleteSourceRepositoryOutcome>, ITenantWriteOperation;

public union DeleteSourceRepositoryOutcome(
    string,
    SourceRepositoryNotFound,
    SourceRepositoryHasBuildConfiguration);

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
            SourceBuildSourceRepositoryConfigurationExists =>
                new SourceRepositoryHasBuildConfiguration(request.Id),
            NoSourceRepositoryBuildConfiguration => await DeleteAsync(
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
            SourceRepositoryDeleted => request.Id,
            SourceRepositoryNotFound error => error
        };
    }
}

public static class DeleteSourceRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapDeleteSourceRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                [AsParameters] DeleteSourceRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    string _ => Results.NoContent(),
                    SourceRepositoryNotFound error => MapSourceRepositoryNotFound(error),
                    SourceRepositoryHasBuildConfiguration error => MapSourceRepositoryHasBuildConfiguration(error)
                });
            })
            .WithName("DeleteSourceRepository")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
