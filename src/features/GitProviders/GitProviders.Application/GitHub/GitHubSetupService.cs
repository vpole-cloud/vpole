using Microsoft.AspNetCore.DataProtection;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Application.Persistence.Setup;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.GitHub;

internal sealed class GitHubSetupService(
    GitHubSetupPersistence setupPersistence,
    IGitHubAppClient gitHubClient,
    GitHubSetupTokenService tokenService,
    IDataProtectionProvider dataProtectionProvider,
    GitHubAppOptions options,
    TimeProvider timeProvider)
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("Pole.GitHubApp.PkceVerifier.v1");

    public async Task<string> HandleInstallationCallbackAsync(
        string? installationId,
        string? state,
        string? setupAction,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return FrontendRedirect("error", "not-configured");
        if (string.IsNullOrWhiteSpace(installationId) || string.IsNullOrWhiteSpace(state))
            return FrontendRedirect("error", "invalid-installation-callback");
        if (!string.IsNullOrWhiteSpace(setupAction) &&
            setupAction is not ("install" or "update"))
        {
            return FrontendRedirect("error", "unsupported-setup-action");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var attempt = await setupPersistence.FindAwaitingInstallationAsync(
            tokenService.Hash(state),
            now,
            cancellationToken);
        if (attempt is null)
            return FrontendRedirect("error", "expired-or-invalid-state");

        try
        {
            var installation = await gitHubClient.GetInstallationAsync(installationId, cancellationToken);
            if (!HasContentsReadPermission(installation))
                return FrontendRedirect("error", "contents-permission-required");

            var oauthState = tokenService.CreateState();
            var verifier = tokenService.CreateCodeVerifier();
            var oauthSetup = new AwaitingGitHubOAuth(
                tokenService.Hash(oauthState),
                _protector.Protect(verifier),
                new GitHubInstallationMetadata(
                    installation.Id,
                    installation.AccountId,
                    installation.AccountLogin,
                    ParseAccountType(installation.AccountType),
                    ParseRepositorySelection(installation.RepositorySelection)));
            var updated = await setupPersistence.PromoteToOAuthAsync(
                attempt.Id,
                oauthSetup,
                cancellationToken);
            if (updated is null)
                return FrontendRedirect("error", "setup-already-used");

            var challenge = tokenService.CreateCodeChallenge(verifier);
            var query = new Dictionary<string, string?>
            {
                ["client_id"] = options.ClientId,
                ["redirect_uri"] = options.OAuthCallbackUrl,
                ["state"] = oauthState,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256"
            };
            return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                $"{options.WebBaseUrl.TrimEnd('/')}/login/oauth/authorize",
                query);
        }
        catch (GitHubApiException)
        {
            return FrontendRedirect("error", "github-installation-unavailable");
        }
    }

    public async Task<string> HandleOAuthCallbackAsync(
        string? code,
        string? state,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
            return FrontendRedirect("error", "not-configured");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return FrontendRedirect("error", "invalid-oauth-callback");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var attempt = await setupPersistence.ConsumeOAuthAsync(
            tokenService.Hash(state),
            now,
            cancellationToken);
        if (attempt?.State is not AwaitingGitHubOAuth oauthSetup)
            return FrontendRedirect("error", "expired-or-invalid-state");

        try
        {
            var verifier = _protector.Unprotect(oauthSetup.ProtectedCodeVerifier);
            var userAccessToken = await gitHubClient.ExchangeOAuthCodeAsync(code, verifier, cancellationToken);
            var canAccess = await gitHubClient.UserCanAccessInstallationAsync(
                userAccessToken,
                oauthSetup.Installation.ExternalInstallationId,
                cancellationToken);
            if (!canAccess)
                return FrontendRedirect("error", "installation-access-denied");

            var result = await setupPersistence.UpsertConnectionAsync(
                attempt,
                oauthSetup.Installation,
                now,
                cancellationToken);
            return result switch
            {
                LinkedGitProviderConnection linked => FrontendRedirect("connected", linked.Id),
                GitProviderConnectionOwnershipRejected => FrontendRedirect("error", "installation-already-linked"),
                GitProviderConnectionNameConflict => FrontendRedirect("error", "connection-name-conflict"),
                GitProviderConnectionNameMismatch => FrontendRedirect("error", "connection-name-mismatch")
            };
        }
        catch (Exception exception) when (exception is GitHubApiException or System.Security.Cryptography.CryptographicException)
        {
            return FrontendRedirect("error", "oauth-verification-failed");
        }
    }

    private string FrontendRedirect(string status, string value)
    {
        var query = new Dictionary<string, string?>
        {
            ["view"] = "integrations",
            ["github"] = status
        };
        query[status == "connected" ? "connectionId" : "reason"] = value;
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            options.FrontendBaseUrl.TrimEnd('/') + "/",
            query);
    }

    private static bool HasContentsReadPermission(GitHubInstallation installation) =>
        installation.Permissions.TryGetValue("contents", out var permission) &&
        permission is "read" or "write";

    private static GitProviderAccountType ParseAccountType(string value) =>
        value.Equals("Organization", StringComparison.OrdinalIgnoreCase)
            ? GitProviderAccountType.Organization
            : GitProviderAccountType.User;

    private static GitProviderRepositorySelection ParseRepositorySelection(string value) =>
        value.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? GitProviderRepositorySelection.All
            : GitProviderRepositorySelection.Selected;
}
