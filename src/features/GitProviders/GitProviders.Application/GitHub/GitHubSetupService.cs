using Microsoft.AspNetCore.DataProtection;
using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.GitHub;

public interface IGitHubSetupService
{
    Task<string> HandleInstallationCallbackAsync(
        string? installationId,
        string? state,
        string? setupAction,
        CancellationToken cancellationToken = default);

    Task<string> HandleOAuthCallbackAsync(
        string? code,
        string? state,
        CancellationToken cancellationToken = default);
}

public sealed class GitHubSetupService(
    IGitProviderDbContext dbContext,
    IGitHubAppClient gitHubClient,
    IGitHubSetupTokenService tokenService,
    IDataProtectionProvider dataProtectionProvider,
    GitHubAppOptions options,
    TimeProvider timeProvider) : IGitHubSetupService
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
        var attempt = await dbContext.GitHubSetupAttempts
            .Find(Builders<GitHubSetupAttempt>.Filter.And(
                Builders<GitHubSetupAttempt>.Filter.Eq(
                    item => ((AwaitingGitHubInstallation)item.State).InstallStateHash,
                    tokenService.Hash(state)),
                Builders<GitHubSetupAttempt>.Filter.OfType<GitHubSetupState, AwaitingGitHubInstallation>(
                    item => item.State),
                Builders<GitHubSetupAttempt>.Filter.Gt(item => item.ExpiresAtUtc, now)))
            .FirstOrDefaultAsync(cancellationToken);
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
            var updated = await dbContext.GitHubSetupAttempts.FindOneAndUpdateAsync(
                Builders<GitHubSetupAttempt>.Filter.And(
                    Builders<GitHubSetupAttempt>.Filter.Eq(item => item.Id, attempt.Id),
                    Builders<GitHubSetupAttempt>.Filter.OfType<GitHubSetupState, AwaitingGitHubInstallation>(
                        item => item.State)),
                Builders<GitHubSetupAttempt>.Update.Set(item => item.State, oauthSetup),
                new FindOneAndUpdateOptions<GitHubSetupAttempt> { ReturnDocument = ReturnDocument.After },
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
        var attempt = await dbContext.GitHubSetupAttempts.FindOneAndUpdateAsync(
            Builders<GitHubSetupAttempt>.Filter.And(
                Builders<GitHubSetupAttempt>.Filter.Eq(
                    item => ((AwaitingGitHubOAuth)item.State).OAuthStateHash,
                    tokenService.Hash(state)),
                Builders<GitHubSetupAttempt>.Filter.OfType<GitHubSetupState, AwaitingGitHubOAuth>(
                    item => item.State),
                Builders<GitHubSetupAttempt>.Filter.Gt(item => item.ExpiresAtUtc, now)),
            Builders<GitHubSetupAttempt>.Update.Set(
                item => item.State,
                new ConsumedGitHubSetup(now)),
            new FindOneAndUpdateOptions<GitHubSetupAttempt>
            {
                ReturnDocument = ReturnDocument.Before
            },
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

            var connectionId = await UpsertConnectionAsync(
                attempt,
                oauthSetup.Installation,
                now,
                cancellationToken);
            if (connectionId is null)
                return FrontendRedirect("error", "installation-already-linked");

            return FrontendRedirect("connected", connectionId);
        }
        catch (Exception exception) when (exception is GitHubApiException or System.Security.Cryptography.CryptographicException)
        {
            return FrontendRedirect("error", "oauth-verification-failed");
        }
    }

    private async Task<string?> UpsertConnectionAsync(
        GitHubSetupAttempt attempt,
        GitHubInstallationMetadata installation,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var globalFilter = Builders<GitProviderConnection>.Filter.And(
            Builders<GitProviderConnection>.Filter.Eq(item => item.Provider, GitProviderKind.GitHub),
            Builders<GitProviderConnection>.Filter.Eq(
                item => item.ExternalInstallationId,
                installation.ExternalInstallationId));
        var existing = await dbContext.GitProviderConnections
            .Find(globalFilter)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.TenantId, attempt.TenantId, StringComparison.Ordinal))
                return null;

            await dbContext.GitProviderConnections.UpdateOneAsync(
                Builders<GitProviderConnection>.Filter.Eq(item => item.Id, existing.Id),
                Builders<GitProviderConnection>.Update
                    .Set(item => item.AccountId, installation.AccountId)
                    .Set(item => item.AccountLogin, installation.AccountLogin)
                    .Set(item => item.AccountType, installation.AccountType)
                    .Set(item => item.RepositorySelection, installation.RepositorySelection)
                    .Set(item => item.UpdatedAtUtc, now),
                cancellationToken: cancellationToken);
            return existing.Id;
        }

        var connection = GitProviderConnectionFactory.Create(
            attempt.TenantId,
            GitProviderKind.GitHub,
            installation.ExternalInstallationId,
            installation.AccountId,
            installation.AccountLogin,
            installation.AccountType,
            installation.RepositorySelection,
            attempt.InitiatedByUserId,
            GitProviderConnectionFactory.DefaultOptions with
            {
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        try
        {
            await dbContext.GitProviderConnections.InsertOneAsync(connection, cancellationToken: cancellationToken);
            return connection.Id;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var raced = await dbContext.GitProviderConnections.Find(globalFilter).FirstOrDefaultAsync(cancellationToken);
            return raced?.TenantId == attempt.TenantId ? raced.Id : null;
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
