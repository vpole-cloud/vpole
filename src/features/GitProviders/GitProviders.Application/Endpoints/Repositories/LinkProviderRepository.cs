using Microsoft.AspNetCore.Mvc;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Analysis;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record LinkProviderRepositoryRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromBody] LinkSourceRepositoryRequestBody Body)
    : IMediatorRequest<SourceRepositoryDto>, ITenantWriteOperation;

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
            dbContext, repositoryService, analyzer, request.TenantId, request.ConnectionId, request.RepositoryId,
            request.Body.Branch, cancellationToken);
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
            request.TenantId, request.ConnectionId, request.RepositoryId, value.Repository.FullName,
            value.Repository.HtmlUrl, value.Repository.IsPrivate, value.Analysis, selectedPlan,
            SourceRepositoryFactory.DefaultOptions);
        var inserted = await dbContext.SourceRepositories.InsertOneAsResultAsync(
            sourceRepository,
            () => Failures.Conflict("This repository branch and build-plan candidate is already linked."),
            cancellationToken);
        return inserted.TryGetValue(out var linkedRepository, out var insertFailure)
            ? linkedRepository.ToDto()
            : insertFailure;
    }
}
