using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence.Connections;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.GitProviders.Application.Endpoints.Repositories;

public sealed record GetProviderRepositoriesRequest(
    [FromRoute] string ConnectionId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 50)
    : IMediatorRequest<GetProviderRepositoriesOutcome>, ITenantReadOperation;

public union GetProviderRepositoriesError(
    GitProviderConnectionNotFound,
    GitProviderRepositoryAccessError);

public union GetProviderRepositoriesOutcome(
    ProviderPage<ProviderRepositoryDto>,
    GetProviderRepositoriesError);

public sealed class GetProviderRepositoriesRequestValidator : AbstractValidator<GetProviderRepositoriesRequest>
{
    public GetProviderRepositoriesRequestValidator()
    {
        RuleFor(request => request.ConnectionId).MongoObjectId("Git provider connection ID");
        RuleFor(request => request.Page).GreaterThanOrEqualTo(1);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class GetProviderRepositoriesHandler(
    LoadGitProviderConnection loadGitProviderConnection,
    GetProviderRepositories getProviderRepositories)
    : IMediatorOutcomeHandler<GetProviderRepositoriesRequest, GetProviderRepositoriesOutcome>
{
    public async Task<GetProviderRepositoriesOutcome> HandleAsync(
        GetProviderRepositoriesRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = await loadGitProviderConnection(
            request.TenantId,
            request.ConnectionId,
            cancellationToken);
        if (connection is null)
            return new GetProviderRepositoriesError(
                new GitProviderConnectionNotFound(request.ConnectionId));

        var pageResult = await getProviderRepositories(
            connection,
            request.Page,
            request.PageSize,
            cancellationToken);
        return pageResult switch
        {
            Ok<ProviderPage<ProviderRepository>> success => new ProviderPage<ProviderRepositoryDto>(
                success.Value.Items.Select(item => new ProviderRepositoryDto(
                    item.Id,
                    item.Name,
                    item.FullName,
                    item.HtmlUrl,
                    item.IsPrivate,
                    item.DefaultBranch,
                    item.UpdatedAtUtc)).ToArray(),
                success.Value.Page,
                success.Value.PageSize,
                success.Value.HasMore),
            Err<GitProviderRepositoryAccessError> error =>
                new GetProviderRepositoriesError(error.Value)
        };
    }
}

public static class GetProviderRepositoriesEndpoint
{
    public static IEndpointRouteBuilder MapGetProviderRepositoriesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ConnectionId}/repositories", async (IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetProviderRepositoriesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ProviderPage<ProviderRepositoryDto> page => Results.Ok(page),
                    GetProviderRepositoriesError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetProviderRepositories")
            .Produces<ProviderPage<ProviderRepositoryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return app;
    }
}
