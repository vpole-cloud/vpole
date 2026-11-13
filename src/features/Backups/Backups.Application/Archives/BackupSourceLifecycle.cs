using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.Backups.Application.Archives;

public sealed class BackupSourceLifecycle(
    IMongoCollection<BackupPolicy> policies,
    MongoRunTransaction transactions,
    BackupConfigurationDeletions deletions
)
{
    public Task RetireAsync(ResourceUnregisteredEvent message, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var tenant = message.EventCore.TenantId;
                var filter = BackupDispatchFilters.ActiveSource(tenant, message.ResourceType, message.ResourceId);
                var policy = await policies.Find(tx.Session, filter).SingleOrDefaultAsync(token);
                if (policy is null)
                    return tx.Abort(false);
                var result = await deletions.DeletePolicyAsync(tenant, policy.Id, policy.Revision, tx.Session, token);
                return result is BackupWriteResult<bool>.Saved ? tx.Commit(true) : tx.Abort(false);
            },
            ct);
}
