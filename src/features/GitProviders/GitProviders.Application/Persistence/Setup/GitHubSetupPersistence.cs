using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Setup;

/// <summary>
/// Mongo protocol for one-time GitHub setup attempts. Its operations share the
/// setup-state transition protocol and are intentionally kept together.
/// </summary>
internal sealed class GitHubSetupPersistence(
    IMongoDatabase database,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
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
        try
        {
            return await UpsertAsync(attempt, installation, now, cancellationToken);
        }
        catch (ConnectionDiscoveryRace)
        {
            return await UpsertAsync(attempt, installation, now, cancellationToken);
        }
    }

    private async Task<string?> UpsertAsync(GitHubSetupAttempt attempt,
        GitHubInstallationMetadata installation,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var outcome = await transactions.InvokeOutcomeAsync<UpsertGitProviderConnectionOutcome>(async (tx, ct) =>
            {
                var globalFilter = GitProviderFilters.ConnectionByInstallation(
                    installation.ExternalInstallationId);
                var existing = await _connections
                    .Find(tx.Session, globalFilter)
                    .FirstOrDefaultAsync(ct);
                if (existing is not null)
                {
                    if (!string.Equals(existing.TenantId, attempt.TenantId, StringComparison.Ordinal))
                        return tx.Abort(new GitProviderConnectionOwnershipRejected());

                    var changed = await _connections.UpdateOneAsync(tx.Session,
                        GitProviderFilters.ConnectionById(existing.Id),
                        Builders<GitProviderConnection>.Update
                            .Set(item => item.AccountId, installation.AccountId)
                            .Set(item => item.AccountLogin, installation.AccountLogin)
                            .Set(item => item.AccountType, installation.AccountType)
                            .Set(item => item.RepositorySelection, installation.RepositorySelection)
                            .Set(item => item.UpdatedAtUtc, now),
                        cancellationToken: ct);
                    if (changed.ModifiedCount == 1)
                        await outbox(tx.Session, [OutboxWrite.Create(existing.ToChangedEvent())], ct);
                    return tx.Commit(new LinkedGitProviderConnection(existing.Id));
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
                    await _connections.InsertOneAsync(tx.Session, connection, cancellationToken: ct);
                }
                catch (MongoWriteException exception) when (
                    exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                {
                    throw new ConnectionDiscoveryRace(exception);
                }

                await outbox(tx.Session, [OutboxWrite.Create(connection.ToChangedEvent())], ct);
                return tx.Commit(new LinkedGitProviderConnection(connection.Id));
            },
            cancellationToken);
        return outcome.Value is LinkedGitProviderConnection linked ? linked.Id : null;
    }

    private sealed class ConnectionDiscoveryRace(MongoWriteException inner)
        : Exception("A Git connection was discovered concurrently.", inner);
}

internal sealed record LinkedGitProviderConnection(string Id);

internal sealed record GitProviderConnectionOwnershipRejected;

internal union UpsertGitProviderConnectionOutcome(
    LinkedGitProviderConnection,
    GitProviderConnectionOwnershipRejected);
