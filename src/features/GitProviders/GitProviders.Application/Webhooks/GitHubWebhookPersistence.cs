using Pole.Features.GitProviders.Application.Mongo;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitProviders.Application.Endpoints.Callbacks;
using Pole.Features.GitProviders.Domain;
using Pole.Features.GitProviders.Events;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.GitProviders.Application.Webhooks;

public sealed class GitHubWebhookPersistence(
    IMongoCollection<GitHubDeliveryReceipt> gitHubDeliveryReceipt,
    IMongoCollection<GitProviderConnection> gitProviderConnections,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    internal const string CollectionName = "GitHubDeliveryReceipts";

    internal async Task<GitHubWebhookResult> StoreAsync(
        GitHubDeliveryReceipt receipt,
        GitHubBranchPush? push,
        CancellationToken ct)
    {
        var receipts = gitHubDeliveryReceipt;

        Task<GitHubWebhookResult> AttemptAsync() => transactions.InvokeOutcomeAsync<GitHubWebhookResult>(
            async (tx, token) =>
            {
                var existing = await receipts.Find(tx.Session, GitProviderFilters.GitHubDeliveryById(receipt.Id))
                    .FirstOrDefaultAsync(token);
                if (existing is not null)
                    return existing.PayloadHash == receipt.PayloadHash && existing.EventType == receipt.EventType
                        ? tx.Commit(GitHubWebhookResult.Accepted)
                        : tx.Abort(GitHubWebhookResult.DeliveryConflict);

                if (push is not null)
                {
                    var connection = await gitProviderConnections
                        .Find(tx.Session, GitProviderFilters.ConnectionByInstallation(push.InstallationId))
                        .FirstOrDefaultAsync(token);
                    if (connection is not null)
                        await outbox(tx.Session,
                            [
                                OutboxWrite.Create(new GitRepositoryPushReceived(receipt.Id,
                                    connection.Provider.ToString(),
                                    connection.Id,
                                    push.RepositoryId,
                                    push.Branch,
                                    push.HeadCommitHint,
                                    EventCore.Create(connection.TenantId)))
                            ],
                            token);
                }

                await receipts.InsertOneAsync(tx.Session, receipt, cancellationToken: token);
                return tx.Commit(GitHubWebhookResult.Accepted);
            },
            ct);

        try
        {
            return await AttemptAsync();
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIdIndex(exception,
                                                        receipts.CollectionNamespace.CollectionName))
        {
            return await AttemptAsync();
        }
    }
}
