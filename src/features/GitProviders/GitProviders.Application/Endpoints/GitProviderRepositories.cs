using FluentValidation;
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

public static class GitProviderRepositories
{
    public static IEndpointRouteBuilder MapGitProviderRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ConnectionId}/repositories", async (IMediator mediator, [AsParameters] GetProviderRepositoriesRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetProviderRepositories")
            .Produces<ProviderPage<ProviderRepositoryDto>>();

        app.MapGet("/{ConnectionId}/repositories/{RepositoryId}/branches", async (IMediator mediator, [AsParameters] GetProviderBranchesRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetProviderRepositoryBranches")
            .Produces<ProviderPage<ProviderBranchDto>>();

        app.MapPost("/{ConnectionId}/repositories/{RepositoryId}/analysis", async (IMediator mediator, [AsParameters] AnalyzeProviderRepositoryRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("AnalyzeProviderRepository")
            .Produces<RepositoryAnalysisDto>();

        app.MapPost("/{ConnectionId}/repositories/{RepositoryId}/links", async (IMediator mediator, [AsParameters] LinkProviderRepositoryRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(item => Results.Created($"/api/v1/source-repositories/{item.Id}", item));
            })
            .WithName("LinkProviderRepository")
            .Produces<SourceRepositoryDto>(StatusCodes.Status201Created);

        return app;
    }
}

public sealed record GetProviderRepositoriesRequest(
    [FromRoute] string ConnectionId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 50)
    : IMediatorRequest<ProviderPage<ProviderRepositoryDto>>, ITenantReadOperation;

public sealed record GetProviderBranchesRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 100)
    : IMediatorRequest<ProviderPage<ProviderBranchDto>>, ITenantReadOperation;

public sealed class GetProviderRepositoriesRequestValidator : AbstractValidator<GetProviderRepositoriesRequest>
{
    public GetProviderRepositoriesRequestValidator()
    {
        RuleFor(request => request.Page).GreaterThanOrEqualTo(1);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class GetProviderBranchesRequestValidator : AbstractValidator<GetProviderBranchesRequest>
{
    public GetProviderBranchesRequestValidator()
    {
        RuleFor(request => request.Page).GreaterThanOrEqualTo(1);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed record AnalyzeProviderRepositoryRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromBody] AnalyzeRepositoryRequestBody Body)
    : IMediatorRequest<RepositoryAnalysisDto>, ITenantReadOperation;

public sealed record LinkProviderRepositoryRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromBody] LinkSourceRepositoryRequestBody Body)
    : IMediatorRequest<SourceRepositoryDto>, ITenantWriteOperation;

public sealed class GetProviderRepositoriesHandler(
    IGitProviderDbContext dbContext,
    IGitProviderRepositoryService repositoryService)
    : IMediatorHandler<GetProviderRepositoriesRequest, ProviderPage<ProviderRepositoryDto>>
{
    public async Task<Result<ProviderPage<ProviderRepositoryDto>>> HandleAsync(
        GetProviderRepositoriesRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = await GitProviderRepositoryAccess.GetConnectionAsync(
            dbContext,
            request.TenantId,
            request.ConnectionId,
            cancellationToken);
        if (connection is null)
            return Failures.NotFound("Git provider connection not found.", request.ConnectionId);

        var pageResult = await repositoryService.GetRepositoriesAsync(
            connection,
            request.Page,
            request.PageSize,
            cancellationToken);
        if (!pageResult.TryGetValue(out var page, out var failure))
            return failure;

        return new ProviderPage<ProviderRepositoryDto>(
            page.Items.Select(item => new ProviderRepositoryDto(
                item.Id,
                item.Name,
                item.FullName,
                item.HtmlUrl,
                item.IsPrivate,
                item.DefaultBranch,
                item.UpdatedAtUtc)).ToArray(),
            page.Page,
            page.PageSize,
            page.HasMore);
    }
}

public sealed class GetProviderBranchesHandler(
    IGitProviderDbContext dbContext,
    IGitProviderRepositoryService repositoryService)
    : IMediatorHandler<GetProviderBranchesRequest, ProviderPage<ProviderBranchDto>>
{
    public async Task<Result<ProviderPage<ProviderBranchDto>>> HandleAsync(
        GetProviderBranchesRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = await GitProviderRepositoryAccess.GetConnectionAsync(
            dbContext,
            request.TenantId,
            request.ConnectionId,
            cancellationToken);
        if (connection is null)
            return Failures.NotFound("Git provider connection not found.", request.ConnectionId);

        var pageResult = await repositoryService.GetBranchesAsync(
            connection,
            request.RepositoryId,
            request.Page,
            request.PageSize,
            cancellationToken);
        if (!pageResult.TryGetValue(out var page, out var failure))
            return failure;

        return new ProviderPage<ProviderBranchDto>(
            page.Items.Select(item => new ProviderBranchDto(item.Name, item.CommitSha, item.IsProtected)).ToArray(),
            page.Page,
            page.PageSize,
            page.HasMore);
    }
}

public sealed class AnalyzeProviderRepositoryHandler(
    IGitProviderDbContext dbContext,
    IGitProviderRepositoryService repositoryService,
    IRepositorySourceAnalyzer analyzer)
    : IMediatorHandler<AnalyzeProviderRepositoryRequest, RepositoryAnalysisDto>
{
    public async Task<Result<RepositoryAnalysisDto>> HandleAsync(
        AnalyzeProviderRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var analyzed = await AnalyzeAsync(
            dbContext,
            repositoryService,
            analyzer,
            request.TenantId,
            request.ConnectionId,
            request.RepositoryId,
            request.Body.Branch,
            cancellationToken);
        if (!analyzed.TryGetValue(out var value, out var failure))
            return failure;
        return value.Analysis.ToDto(value.Repository);
    }

    internal static async Task<Result<AnalyzedProviderRepository>> AnalyzeAsync(
        IGitProviderDbContext dbContext,
        IGitProviderRepositoryService repositoryService,
        IRepositorySourceAnalyzer analyzer,
        string tenantId,
        string connectionId,
        string repositoryId,
        string branch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Body.Branch"] = ["Branch is required."]
            });
        }

        var connection = await GitProviderRepositoryAccess.GetConnectionAsync(
            dbContext,
            tenantId,
            connectionId,
            cancellationToken);
        if (connection is null)
            return Failures.NotFound("Git provider connection not found.", connectionId);

        var snapshotResult = await repositoryService.GetRepositorySnapshotAsync(
            connection,
            repositoryId,
            branch.Trim(),
            cancellationToken);
        if (!snapshotResult.TryGetValue(out var snapshot, out var snapshotFailure))
            return snapshotFailure;

        var analysisResult = analyzer.Analyze(snapshot);
        if (!analysisResult.TryGetValue(out var analysis, out var analysisFailure))
            return analysisFailure;
        return new AnalyzedProviderRepository(connection, snapshot.Repository, analysis);
    }
}

public sealed class LinkProviderRepositoryHandler(
    IGitProviderDbContext dbContext,
    IGitProviderRepositoryService repositoryService,
    IRepositorySourceAnalyzer analyzer)
    : IMediatorHandler<LinkProviderRepositoryRequest, SourceRepositoryDto>
{
    public async Task<Result<SourceRepositoryDto>> HandleAsync(
        LinkProviderRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var analyzed = await AnalyzeProviderRepositoryHandler.AnalyzeAsync(
            dbContext,
            repositoryService,
            analyzer,
            request.TenantId,
            request.ConnectionId,
            request.RepositoryId,
            request.Body.Branch,
            cancellationToken);
        if (!analyzed.TryGetValue(out var value, out var failure))
            return failure;

        var selectedPlan = value.Analysis.Candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.CandidateKey, request.Body.CandidateKey, StringComparison.Ordinal));
        if (selectedPlan is null)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Body.CandidateKey"] = ["The selected build-plan candidate was not found in the latest analysis."]
            });
        }

        var sourceRepository = SourceRepositoryFactory.Create(
            request.TenantId,
            request.ConnectionId,
            request.RepositoryId,
            value.Repository.FullName,
            value.Repository.HtmlUrl,
            value.Repository.IsPrivate,
            value.Analysis,
            selectedPlan,
            SourceRepositoryFactory.DefaultOptions);
        try
        {
            await dbContext.SourceRepositories.InsertOneAsync(sourceRepository, cancellationToken: cancellationToken);
            return sourceRepository.ToDto();
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict("This repository branch and build-plan candidate is already linked.");
        }
    }
}

internal sealed record AnalyzedProviderRepository(
    GitProviderConnection Connection,
    ProviderRepository Repository,
    SourceRepositoryAnalysis Analysis);

internal static class GitProviderRepositoryAccess
{
    internal static async Task<GitProviderConnection?> GetConnectionAsync(
        IGitProviderDbContext dbContext,
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken) =>
        await dbContext.GitProviderConnections
            .Find(Builders<GitProviderConnection>.Filter.And(
                Builders<GitProviderConnection>.Filter.ForTenant(tenantId),
                Builders<GitProviderConnection>.Filter.Eq(item => item.Id, connectionId)))
            .FirstOrDefaultAsync(cancellationToken);

}
