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

public sealed record GetProviderBranchesRequest(
    [FromRoute] string ConnectionId,
    [FromRoute] string RepositoryId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromQuery] int Page = 1,
    [FromQuery] int PageSize = 100)
    : IMediatorRequest<GetProviderBranchesOutcome>, ITenantReadOperation;

public union GetProviderBranchesError(
    GitProviderConnectionNotFound,
    GitProviderRepositoryAccessError);

public union GetProviderBranchesOutcome(
    ProviderPage<ProviderBranchDto>,
    GetProviderBranchesError);

public sealed class GetProviderBranchesRequestValidator : AbstractValidator<GetProviderBranchesRequest>
{
    public GetProviderBranchesRequestValidator()
    {
        RuleFor(request => request.ConnectionId).MongoObjectId("Git provider connection ID");
        RuleFor(request => request.RepositoryId).NotEmpty();
        RuleFor(request => request.Page).GreaterThanOrEqualTo(1);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class GetProviderBranchesHandler(
    LoadGitProviderConnection loadGitProviderConnection,
    GetProviderBranches getProviderBranches)
    : IMediatorOutcomeHandler<GetProviderBranchesRequest, GetProviderBranchesOutcome>
{
    public async Task<GetProviderBranchesOutcome> HandleAsync(
        GetProviderBranchesRequest request,
        CancellationToken cancellationToken = default)
    {
        var connection = await loadGitProviderConnection(
            request.TenantId,
            request.ConnectionId,
            cancellationToken);
        if (connection is null)
            return new GetProviderBranchesError(
                new GitProviderConnectionNotFound(request.ConnectionId));

        var pageResult = await getProviderBranches(
            connection,
            request.RepositoryId,
            request.Page,
            request.PageSize,
            cancellationToken);
        return pageResult switch
        {
            Ok<ProviderPage<ProviderBranch>> success => new ProviderPage<ProviderBranchDto>(
                success.Value.Items.Select(item =>
                    new ProviderBranchDto(item.Name, item.CommitSha, item.IsProtected)).ToArray(),
                success.Value.Page,
                success.Value.PageSize,
                success.Value.HasMore),
            Err<GitProviderRepositoryAccessError> error =>
                new GetProviderBranchesError(error.Value)
        };
    }
}

public static class GetProviderBranchesEndpoint
{
    public static IEndpointRouteBuilder MapGetProviderBranchesEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{ConnectionId}/repositories/{RepositoryId}/branches", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] GetProviderBranchesRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    ProviderPage<ProviderBranchDto> page => Results.Ok(page),
                    GetProviderBranchesError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("GetProviderRepositoryBranches")
            .Produces<ProviderPage<ProviderBranchDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return app;
    }
}
