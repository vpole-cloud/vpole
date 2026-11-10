using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Mongo;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.GitProviders.Application.Setup;

/// <summary>
///     Mongo protocol for one-time GitHub setup attempts. Its operations share the
///     setup-state transition protocol and are intentionally kept together.
/// </summary>
internal sealed class GitHubSetupPersistence(
    IMongoCollection<GitHubSetupAttempt> _attempts,
    IMongoCollection<GitProviderConnection> _connections,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox
)
{
    internal async Task<GitHubSetupAttempt?> FindAwaitingInstallationAsync(
        string stateHash,
        DateTime now,
        CancellationToken cancellationToken) => await _attempts
        .Find(GitProviderFilters.AwaitingGitHubInstallation(stateHash, now))
        .FirstOrDefaultAsync(cancellationToken);

    internal async Task<GitHubSetupAttempt?> PromoteToOAuthAsync(
        string attemptId,
        AwaitingGitHubOAuth oauthSetup,
        CancellationToken cancellationToken) => await _attempts.FindOneAndUpdateAsync(
        GitProviderFilters.AwaitingGitHubInstallationById(attemptId),
        Builders<GitHubSetupAttempt>.Update.Set(item => item.State, oauthSetup),
        new FindOneAndUpdateOptions<GitHubSetupAttempt>
        {
            ReturnDocument = ReturnDocument.After,
        },
        cancellationToken);

    internal async Task<GitHubSetupAttempt?> ConsumeOAuthAsync(
        string stateHash,
        DateTime now,
        CancellationToken cancellationToken) => await _attempts.FindOneAndUpdateAsync(
        GitProviderFilters.AwaitingGitHubOAuth(stateHash, now),
        Builders<GitHubSetupAttempt>.Update.Set(item => item.State, new ConsumedGitHubSetup(now)),
        new FindOneAndUpdateOptions<GitHubSetupAttempt>
        {
            ReturnDocument = ReturnDocument.Before,
        },
        cancellationToken);

    internal async Task<UpsertGitProviderConnectionOutcome> UpsertConnectionAsync(
        GitHubSetupAttempt attempt,
        GitHubInstallationMetadata installation,
        DateTime now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await UpsertAsync(attempt, installation, now, cancellationToken);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                        exception,
                                                        GitProviderConnectionIndexes.TechnicalName,
                                                        _connections.CollectionNamespace.CollectionName))
        {
            // The same installation/name can race on either unique index. Re-read its identity before classifying.
            var existing = await _connections.Find(
                    GitProviderFilters.ConnectionByInstallation(installation.ExternalInstallationId))
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
                return await UpsertAsync(attempt, installation, now, cancellationToken);

            return new GitProviderConnectionNameConflict(attempt.TechnicalName);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
                                                        exception,
                                                        GitProviderConnectionIndexes.Installation,
                                                        _connections.CollectionNamespace.CollectionName))
        {
            return await UpsertAsync(attempt, installation, now, cancellationToken);
        }
    }

    private async Task<UpsertGitProviderConnectionOutcome> UpsertAsync(
        GitHubSetupAttempt attempt,
        GitHubInstallationMetadata installation,
        DateTime now,
        CancellationToken cancellationToken)
    {
        return await transactions.InvokeOutcomeAsync<UpsertGitProviderConnectionOutcome>(
            async (tx, ct) =>
            {
                var globalFilter = GitProviderFilters.ConnectionByInstallation(installation.ExternalInstallationId);
                var existing = await _connections.Find(tx.Session, globalFilter).FirstOrDefaultAsync(ct);
                if (existing is not null)
                {
                    if (!string.Equals(existing.TenantId, attempt.TenantId, StringComparison.Ordinal))
                        return tx.Abort(new GitProviderConnectionOwnershipRejected());

                    if (!string.Equals(existing.TechnicalName, attempt.TechnicalName, StringComparison.Ordinal))
                        return tx.Abort(new GitProviderConnectionNameMismatch(existing.TechnicalName));

                    var changed = await _connections.UpdateOneAsync(
                        tx.Session,
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
                    attempt.TechnicalName,
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
                        UpdatedAtUtc = now,
                    });
                await _connections.InsertOneAsync(tx.Session, connection, cancellationToken: ct);

                await outbox(tx.Session, [OutboxWrite.Create(connection.ToChangedEvent())], ct);
                return tx.Commit(new LinkedGitProviderConnection(connection.Id));
            },
            cancellationToken);
    }
}

internal sealed record LinkedGitProviderConnection(
    string Id
);

internal sealed record GitProviderConnectionOwnershipRejected;

internal union UpsertGitProviderConnectionOutcome(
    LinkedGitProviderConnection,
    GitProviderConnectionOwnershipRejected,
    GitProviderConnectionNameConflict,
    GitProviderConnectionNameMismatch);

internal sealed record GitProviderConnectionNameConflict(
    string TechnicalName
);

internal sealed record GitProviderConnectionNameMismatch(
    string TechnicalName
);
