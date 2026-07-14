using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record DeleteSourceRepositoryRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<string>, ITenantWriteOperation;

public sealed class DeleteSourceRepositoryHandler(
    IGitProviderDbContext dbContext,
    ISourceBuildReferenceLookup sourceBuildReferences)
    : IMediatorHandler<DeleteSourceRepositoryRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteSourceRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var configurationGuard = await sourceBuildReferences.EnsureSourceRepositoryCanBeDeletedAsync(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (configurationGuard is Failure failure)
            return failure;

        var result = await dbContext.SourceRepositories.DeleteOneAsync(
            Builders<SourceRepository>.Filter.And(
                Builders<SourceRepository>.Filter.ForTenant(request.TenantId),
                Builders<SourceRepository>.Filter.Eq(item => item.Id, request.Id)),
            cancellationToken);
        return result.DeletedCount == 0
            ? Failures.NotFound("Source repository not found.", request.Id)
            : request.Id;
    }
}

public static class DeleteSourceRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapDeleteSourceRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (IMediator mediator, [AsParameters] DeleteSourceRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteSourceRepository")
            .Produces(StatusCodes.Status204NoContent);
        return app;
    }
}
