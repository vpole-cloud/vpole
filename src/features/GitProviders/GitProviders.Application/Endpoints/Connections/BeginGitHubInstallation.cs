using Microsoft.AspNetCore.Mvc;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record BeginGitHubInstallationRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId)
    : IMediatorRequest<BeginGitHubInstallationResponse>, ITenantWriteOperation;

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
