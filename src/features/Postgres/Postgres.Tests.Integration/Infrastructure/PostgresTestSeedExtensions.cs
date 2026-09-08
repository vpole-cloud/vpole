using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.Postgres.Tests.Integration.Infrastructure;

public static class PostgresTestSeedExtensions
{
    public static async Task<ResourceGroup> InsertResourceGroupAsync(this PostgresTestStore store, ResourceGroup group)
    {
        var scope = IsolationScopeFactory.CreateDefault(group.TenantId, group.Id,
            new IsolationScopeFactory.CreateOptions(group.DefaultIsolationScopeId, group.CreatedAtUtc));
        _ = await store.RunOutcomeInTransactionAsync<Unit>(async (transaction, cancellationToken) =>
        {
            await store.ResourceGroups.InsertOneAsync(transaction.Session, group, cancellationToken: cancellationToken);
            await store.IsolationScopes.InsertOneAsync(transaction.Session, scope, cancellationToken: cancellationToken);
            return transaction.Commit(Unit.Value);
        });
        return group;
    }

    public static Task<long> CountOutboxMessagesAsync(this PostgresTestStore store, string eventType) =>
        store.OutboxMessages.CountDocumentsAsync(message => message.EventType == eventType);
}
