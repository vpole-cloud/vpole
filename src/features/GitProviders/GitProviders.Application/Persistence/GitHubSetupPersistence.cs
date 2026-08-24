using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence;

/// <summary>
/// Mongo protocol for one-time GitHub setup attempts. Its operations share the
/// setup-state transition protocol and are intentionally kept together.
/// </summary>
internal sealed class GitHubSetupPersistence(IMongoDatabase database)
{
    private readonly IMongoCollection<GitHubSetupAttempt> _attempts =
        database.GetCollection<GitHubSetupAttempt>("GitHubSetupAttempts");

    private readonly IMongoCollection<GitProviderConnection> _connections =
        database.GetCollection<GitProviderConnection>("GitProviderConnections");

    internal async Task<GitHubSetupAttempt?> FindAwaitingInstallationAsync(
        string stateHash,
        DateTime now,
        CancellationToken cancellationToken) =>
        await _attempts
            .Find(GitProviderFilters.AwaitingGitHubInstallation(stateHash, now))
            .FirstOrDefaultAsync(cancellationToken);

    internal async Task<GitHubSetupAttempt?> PromoteToOAuthAsync(
        string attemptId,
        AwaitingGitHubOAuth oauthSetup,
        CancellationToken cancellationToken) =>
        await _attempts.FindOneAndUpdateAsync(
            GitProviderFilters.AwaitingGitHubInstallationById(attemptId),
            Builders<GitHubSetupAttempt>.Update.Set(item => item.State, oauthSetup),
            new FindOneAndUpdateOptions<GitHubSetupAttempt>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);

    internal async Task<GitHubSetupAttempt?> ConsumeOAuthAsync(
        string stateHash,
        DateTime now,
        CancellationToken cancellationToken) =>
        await _attempts.FindOneAndUpdateAsync(
            GitProviderFilters.AwaitingGitHubOAuth(stateHash, now),
            Builders<GitHubSetupAttempt>.Update.Set(
                item => item.State,
                new ConsumedGitHubSetup(now)),
            new FindOneAndUpdateOptions<GitHubSetupAttempt>
            {
                ReturnDocument = ReturnDocument.Before
            },
            cancellationToken);

    internal async Task<string?> UpsertConnectionAsync(
        GitHubSetupAttempt attempt,
        GitHubInstallationMetadata installation,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var globalFilter = GitProviderFilters.ConnectionByInstallation(
            installation.ExternalInstallationId);
        var existing = await _connections
            .Find(globalFilter)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.TenantId, attempt.TenantId, StringComparison.Ordinal))
                return null;

            await _connections.UpdateOneAsync(
                GitProviderFilters.ConnectionById(existing.Id),
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
            await _connections.InsertOneAsync(connection, cancellationToken: cancellationToken);
            return connection.Id;
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var raced = await _connections
                .Find(globalFilter)
                .FirstOrDefaultAsync(cancellationToken);
            return raced?.TenantId == attempt.TenantId ? raced.Id : null;
        }
    }
}
