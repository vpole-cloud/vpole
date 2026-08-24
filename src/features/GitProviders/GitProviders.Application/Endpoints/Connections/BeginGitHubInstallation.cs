using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;

namespace Pole.Features.GitProviders.Application.Endpoints;

public sealed record BeginGitHubInstallationRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId)
    : IMediatorRequest<BeginGitHubInstallationOutcome>, ITenantWriteOperation;

public union BeginGitHubInstallationOutcome(
    BeginGitHubInstallationResponse,
    GitHubIntegrationNotConfigured);

public sealed class BeginGitHubInstallationHandler(
    CreateGitHubSetupAttempt createGitHubSetupAttempt,
    GitHubSetupTokenService tokenService,
    GitHubAppOptions options,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<BeginGitHubInstallationRequest, BeginGitHubInstallationOutcome>
{
    public async Task<BeginGitHubInstallationOutcome> HandleAsync(
        BeginGitHubInstallationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return new GitHubIntegrationNotConfigured();

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
        await createGitHubSetupAttempt(attempt, cancellationToken);

        var installUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            $"{options.WebBaseUrl.TrimEnd('/')}/apps/{Uri.EscapeDataString(options.AppSlug)}/installations/new",
            "state",
            state);
        return new BeginGitHubInstallationResponse(installUrl, attempt.ExpiresAtUtc);
    }
}

public static class BeginGitHubInstallationEndpoint
{
    public static IEndpointRouteBuilder MapBeginGitHubInstallationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/github/install", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] BeginGitHubInstallationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    BeginGitHubInstallationResponse response => Results.Ok(response),
                    GitHubIntegrationNotConfigured error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("BeginGitHubInstallation")
            .Produces<BeginGitHubInstallationResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
