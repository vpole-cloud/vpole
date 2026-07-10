using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public static class GitProviderConnections
{
    public static IEndpointRouteBuilder MapGitProviderConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (IMediator mediator, [AsParameters] GetGitProviderConnectionsRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetGitProviderConnections")
            .Produces<IReadOnlyList<GitProviderConnectionDto>>();

        app.MapPost("/github/install", async (IMediator mediator, [AsParameters] BeginGitHubInstallationRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("BeginGitHubInstallation")
            .Produces<BeginGitHubInstallationResponse>();

        app.MapDelete("/{ConnectionId}", async (IMediator mediator, [AsParameters] DeleteGitProviderConnectionRequest request, CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(_ => Results.NoContent());
            })
            .WithName("DeleteGitProviderConnection")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}

public sealed record GetGitProviderConnectionsRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyList<GitProviderConnectionDto>>, ITenantReadOperation;

public sealed record BeginGitHubInstallationRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId)
    : IMediatorRequest<BeginGitHubInstallationResponse>, ITenantWriteOperation;

public sealed record DeleteGitProviderConnectionRequest(
    [FromRoute] string ConnectionId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<string>, ITenantWriteOperation;

public sealed class GetGitProviderConnectionsHandler(IGitProviderDbContext dbContext)
    : IMediatorHandler<GetGitProviderConnectionsRequest, IReadOnlyList<GitProviderConnectionDto>>
{
    public async Task<Result<IReadOnlyList<GitProviderConnectionDto>>> HandleAsync(
        GetGitProviderConnectionsRequest request,
        CancellationToken cancellationToken = default)
    {
        var connections = await dbContext.GitProviderConnections
            .Find(Builders<GitProviderConnection>.Filter.ForTenant(request.TenantId))
            .SortBy(item => item.Provider)
            .ThenBy(item => item.AccountLogin)
            .ToListAsync(cancellationToken);
        return connections.Select(item => item.ToDto()).ToArray();
    }
}

public sealed class BeginGitHubInstallationHandler(
    IGitProviderDbContext dbContext,
    IGitHubSetupTokenService tokenService,
    GitHubAppOptions options,
    TimeProvider timeProvider)
    : IMediatorHandler<BeginGitHubInstallationRequest, BeginGitHubInstallationResponse>
{
    public async Task<Result<BeginGitHubInstallationResponse>> HandleAsync(
        BeginGitHubInstallationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return Failures.Conflict("GitHub App integration is not configured.");

        var state = tokenService.CreateState();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var attempt = GitHubSetupAttemptFactory.Create(
            request.TenantId,
            request.CurrentUserId.Value,
            tokenService.Hash(state),
            new GitHubSetupAttemptFactory.CreateOptions(
                MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                now,
                now.AddMinutes(10)));
        await dbContext.GitHubSetupAttempts.InsertOneAsync(attempt, cancellationToken: cancellationToken);

        var installUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            $"{options.WebBaseUrl.TrimEnd('/')}/apps/{Uri.EscapeDataString(options.AppSlug)}/installations/new",
            "state",
            state);
        return new BeginGitHubInstallationResponse(installUrl, attempt.ExpiresAtUtc);
    }
}

public sealed class DeleteGitProviderConnectionHandler(IGitProviderDbContext dbContext)
    : IMediatorHandler<DeleteGitProviderConnectionRequest, string>
{
    public async Task<Result<string>> HandleAsync(
        DeleteGitProviderConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var connectionFilter = Builders<GitProviderConnection>.Filter.And(
            Builders<GitProviderConnection>.Filter.ForTenant(request.TenantId),
            Builders<GitProviderConnection>.Filter.Eq(item => item.Id, request.ConnectionId));
        if (!await dbContext.GitProviderConnections.Find(connectionFilter).AnyAsync(cancellationToken))
            return Failures.NotFound("Git provider connection not found.", request.ConnectionId);

        var linkFilter = Builders<SourceRepository>.Filter.And(
            Builders<SourceRepository>.Filter.ForTenant(request.TenantId),
            Builders<SourceRepository>.Filter.Eq(item => item.ConnectionId, request.ConnectionId));
        if (await dbContext.SourceRepositories.Find(linkFilter).AnyAsync(cancellationToken))
            return Failures.Conflict("Unlink source repositories before deleting this provider connection.");

        await dbContext.GitProviderConnections.DeleteOneAsync(connectionFilter, cancellationToken);
        return request.ConnectionId;
    }
}
