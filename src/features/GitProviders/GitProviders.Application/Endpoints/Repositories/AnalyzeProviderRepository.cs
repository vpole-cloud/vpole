using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Analysis;
using Pole.Features.GitProviders.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record AnalyzeProviderRepositoryRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromBody] AnalyzeRepositoryRequestBody Body)
    : IMediatorRequest<RepositoryAnalysisDto>, ITenantReadOperation;

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
        var analyzed = await AnalyzeAsync(dbContext, repositoryService, analyzer, request.TenantId,
            request.ConnectionId, request.RepositoryId, request.Body.Branch, cancellationToken);
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
            dbContext, tenantId, connectionId, cancellationToken);
        if (connection is null)
            return Failures.NotFound("Git provider connection not found.", connectionId);

        var snapshotResult = await repositoryService.GetRepositorySnapshotAsync(
            connection, repositoryId, branch.Trim(), cancellationToken);
        if (!snapshotResult.TryGetValue(out var snapshot, out var snapshotFailure))
            return snapshotFailure;

        var analysisResult = analyzer.Analyze(snapshot);
        if (!analysisResult.TryGetValue(out var analysis, out var analysisFailure))
            return analysisFailure;
        return new AnalyzedProviderRepository(connection, snapshot.Repository, analysis);
    }
}

public static class AnalyzeProviderRepositoryEndpoint
{
    public static IEndpointRouteBuilder MapAnalyzeProviderRepositoryEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{ConnectionId}/repositories/{RepositoryId}/analysis", async (IMediator mediator,
                [AsParameters] AnalyzeProviderRepositoryRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("AnalyzeProviderRepository")
            .Produces<RepositoryAnalysisDto>();
        return app;
    }
}
