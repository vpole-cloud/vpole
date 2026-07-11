using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Analysis;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public static class SourceRepositories
{
    public static IEndpointRouteBuilder MapSourceRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetSourceRepositoriesRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetSourceRepositories")
            .Produces<IReadOnlyList<SourceRepositoryDto>>();

        app.MapPost("/{Id}/reanalyze", async (IMediator mediator, [AsParameters] ReanalyzeSourceRepositoryRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("ReanalyzeSourceRepository")
            .Produces<SourceRepositoryDto>();

        app.MapDelete("/{Id}", async (IMediator mediator, [AsParameters] DeleteSourceRepositoryRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteSourceRepository")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}

public sealed record GetSourceRepositoriesRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<SourceRepositoryDto>>, ITenantReadOperation;

public sealed record ReanalyzeSourceRepositoryRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SourceRepositoryDto>, ITenantWriteOperation;

public sealed record DeleteSourceRepositoryRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<string>, ITenantWriteOperation;

public sealed class GetSourceRepositoriesHandler(IGitProviderDbContext dbContext)
    : IMediatorHandler<GetSourceRepositoriesRequest, IReadOnlyList<SourceRepositoryDto>>
{
    public async Task<Result<IReadOnlyList<SourceRepositoryDto>>> HandleAsync(
        GetSourceRepositoriesRequest request,
        CancellationToken cancellationToken = default)
    {
        var repositories = await dbContext.SourceRepositories
            .Find(Builders<SourceRepository>.Filter.ForTenant(request.TenantId))
            .SortBy(item => item.RepositoryFullName)
            .ThenBy(item => item.Branch)
            .ToListAsync(cancellationToken);
        return repositories.Select(item => item.ToDto()).ToArray();
    }
}

public sealed class ReanalyzeSourceRepositoryHandler(
    IGitProviderDbContext dbContext,
    IGitProviderRepositoryService repositoryService,
    IRepositorySourceAnalyzer analyzer,
    TimeProvider timeProvider)
    : IMediatorHandler<ReanalyzeSourceRepositoryRequest, SourceRepositoryDto>
{
    public async Task<Result<SourceRepositoryDto>> HandleAsync(
        ReanalyzeSourceRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<SourceRepository>.Filter.And(
            Builders<SourceRepository>.Filter.ForTenant(request.TenantId),
            Builders<SourceRepository>.Filter.Eq(item => item.Id, request.Id));
        var existing = await dbContext.SourceRepositories.Find(filter).FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
            return Failures.NotFound("Source repository not found.", request.Id);

        var analyzed = await AnalyzeProviderRepositoryHandler.AnalyzeAsync(
            dbContext,
            repositoryService,
            analyzer,
            request.TenantId,
            existing.ConnectionId,
            existing.ExternalRepositoryId,
            existing.Branch,
            cancellationToken);
        if (!analyzed.TryGetValue(out var value, out var failure))
            return failure;

        var selectedPlan = value.Analysis.Candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.CandidateKey, existing.CandidateKey, StringComparison.Ordinal));
        if (selectedPlan is null)
            return Failures.Conflict("The previously selected build-plan candidate no longer exists.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = await dbContext.SourceRepositories.FindOneAndUpdateAsync(
            filter,
            Builders<SourceRepository>.Update
                .Set(item => item.RepositoryFullName, value.Repository.FullName)
                .Set(item => item.RepositoryUrl, value.Repository.HtmlUrl)
                .Set(item => item.IsPrivate, value.Repository.IsPrivate)
                .Set(item => item.SelectedPlan, selectedPlan)
                .Set(item => item.CommitSha, value.Analysis.CommitSha)
                .Set(item => item.AnalyzedAtUtc, value.Analysis.AnalyzedAtUtc)
                .Set(item => item.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<SourceRepository> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        return updated.ToDto();
    }
}

public sealed class DeleteSourceRepositoryHandler(
    IGitProviderDbContext dbContext,
    ISourceBuildReferenceLookup sourceBuildReferences)
    : IMediatorHandler<DeleteSourceRepositoryRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteSourceRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await sourceBuildReferences.HasConfigurationForSourceRepositoryAsync(
                request.TenantId,
                request.Id,
                cancellationToken))
        {
            return Failures.Conflict(
                "Delete the source build configuration before unlinking this repository.");
        }

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
