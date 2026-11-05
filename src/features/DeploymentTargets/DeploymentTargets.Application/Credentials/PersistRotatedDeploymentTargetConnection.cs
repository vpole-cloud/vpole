using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application;
using MongoDB.Driver;
using MongoDB.Bson;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.DeploymentTargets.Events;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Credentials;

public union PersistRotatedDeploymentTargetConnectionOutcome(
    DeploymentTarget,
    DeploymentTargetRevisionConflict);

public delegate Task<PersistRotatedDeploymentTargetConnectionOutcome>
    PersistRotatedDeploymentTargetConnection(
        DeploymentTarget current,
        DeploymentTarget replacement,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistRotatedDeploymentTargetConnection(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    RunMongoOutcomeTransaction<PersistRotatedDeploymentTargetConnectionOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<PersistRotatedDeploymentTargetConnectionOutcome> InvokeAsync(
        DeploymentTarget current,
        DeploymentTarget replacement,
        CancellationToken cancellationToken = default)
    {
        EnsureSameIdentity(current, replacement);
        return await runMongoTransaction(async (transaction, ct) =>
            {
                var before = await deploymentTargets.Find(transaction.Session,
                    DeploymentTargetFilters.AtRevisionAndConnectionVersion(current)).FirstOrDefaultAsync(ct);
                if (before is null)
                    return transaction.Abort(new DeploymentTargetRevisionConflict(current.Id));
                var persisted = await deploymentTargets.FindOneAndUpdateAsync(
                    transaction.Session,
                    DeploymentTargetFilters.AtRevisionAndConnectionVersion(current),
                    Builders<DeploymentTarget>.Update.Combine(
                        Builders<DeploymentTarget>.Update.Set(item => item.Connection, replacement.Connection),
                        Builders<DeploymentTarget>.Update.Set(item => item.Health, replacement.Health),
                        Builders<DeploymentTarget>.Update.Set(item => item.Capabilities, replacement.Capabilities),
                        Builders<DeploymentTarget>.Update.Set(item => item.Revision, replacement.Revision),
                        Builders<DeploymentTarget>.Update.Set(item => item.UpdatedAtUtc, replacement.UpdatedAtUtc)),
                    new FindOneAndUpdateOptions<DeploymentTarget>
                    {
                        ReturnDocument = ReturnDocument.After
                    },
                    ct);
                if (persisted is null)
                    return transaction.Abort(new DeploymentTargetRevisionConflict(current.Id));
                if (ConnectionFieldsChanged(before, persisted))
                    await storeOutboxWrites(transaction.Session,
                        [OutboxWrite.Create(new DeploymentTargetCatalogChangedEvent(persisted.Id, persisted.Revision))],
                        ct);
                return transaction.Commit(persisted);
            },
            cancellationToken);
    }

    private static bool ConnectionFieldsChanged(DeploymentTarget before, DeploymentTarget after) =>
        before.Revision != after.Revision || before.UpdatedAtUtc != after.UpdatedAtUtc ||
        before.Health != after.Health || before.Capabilities != after.Capabilities ||
        !before.Connection.ToBsonDocument().Equals(after.Connection.ToBsonDocument());

    private static void EnsureSameIdentity(
        DeploymentTarget current,
        DeploymentTarget replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.Id, replacement.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The current and replacement deployment targets must have the same ID.",
                nameof(replacement));
        }
    }
}
