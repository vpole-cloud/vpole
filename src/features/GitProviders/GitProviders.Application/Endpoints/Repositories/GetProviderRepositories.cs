using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record GetProviderRepositoriesRequest(
    [FromRoute] string ConnectionId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 50)
    : IMediatorRequest<ProviderPage<ProviderRepositoryDto>>, ITenantReadOperation;

public sealed class GetProviderRepositoriesRequestValidator : AbstractValidator<GetProviderRepositoriesRequest>
{
    public GetProviderRepositoriesRequestValidator()
    {
        RuleFor(request => request.Page).GreaterThanOrEqualTo(1);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
    }
}

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
                item.Id, item.Name, item.FullName, item.HtmlUrl, item.IsPrivate, item.DefaultBranch, item.UpdatedAtUtc)).ToArray(),
            page.Page,
            page.PageSize,
            page.HasMore);
    }
}

public static class GetProviderRepositoriesEndpoint
{
    public static IEndpointRouteBuilder MapGetProviderRepositoriesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ConnectionId}/repositories", async (IMediator mediator,
                [AsParameters] GetProviderRepositoriesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetProviderRepositories")
            .Produces<ProviderPage<ProviderRepositoryDto>>();
        return app;
    }
}
