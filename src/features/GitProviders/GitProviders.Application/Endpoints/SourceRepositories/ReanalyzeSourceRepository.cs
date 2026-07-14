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
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record ReanalyzeSourceRepositoryRequest(
    [FromRoute] string Id,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<SourceRepositoryDto>, ITenantWriteOperation;

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
            dbContext, repositoryService, analyzer, request.TenantId, existing.ConnectionId,
            existing.ExternalRepositoryId, existing.Branch, cancellationToken);
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

public static class ReanalyzeSourceRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapReanalyzeSourceRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/reanalyze", async (IMediator mediator, [AsParameters] ReanalyzeSourceRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("ReanalyzeSourceRepository")
            .Produces<SourceRepositoryDto>();
        return app;
    }
}
