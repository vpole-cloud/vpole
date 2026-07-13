using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record GetProviderBranchesRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 100)
    : IMediatorRequest<ProviderPage<ProviderBranchDto>>, ITenantReadOperation;

public sealed class GetProviderBranchesRequestValidator : AbstractValidator<GetProviderBranchesRequest>
{
    public GetProviderBranchesRequestValidator()
    {
        RuleFor(request => request.Page).GreaterThanOrEqualTo(1);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
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
