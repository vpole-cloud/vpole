using FluentValidation;
using Pole.Workloads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Pole.Features.GitProviders.Application.Persistence.Setup;
using Pole.Features.GitProviders.Domain;
using Pole.Mediator;
using static Pole.Features.GitProviders.Application.Common.GitProviderHttpMappings;

namespace Pole.Features.GitProviders.Application.Endpoints.Connections;

public sealed record BeginGitHubInstallationRequest(
    [FromBody] BeginGitHubInstallationBody Body,
    [FromTenantHeader] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue CurrentUserId)
    : IMediatorRequest<BeginGitHubInstallationOutcome>, ITenantWriteOperation;

public sealed record BeginGitHubInstallationBody(string TechnicalName);

public sealed class BeginGitHubInstallationValidator : AbstractValidator<BeginGitHubInstallationRequest>
{
    public BeginGitHubInstallationValidator()
    {
        RuleFor(request => request.Body.TechnicalName).Must(ResourceTechnicalName.IsValid)
            .WithMessage("Technical name must be a lowercase DNS label beginning with a letter, up to 63 characters.");
    }
}

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
            request.Body.TechnicalName,
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
                [AsParameters] BeginGitHubInstallationRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    BeginGitHubInstallationResponse response => Results.Ok(response),
                    GitHubIntegrationNotConfigured error => MapGitHubIntegrationNotConfigured(error)
                });
            })
            .WithName("BeginGitHubInstallation")
            .Produces<BeginGitHubInstallationResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
