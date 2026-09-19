using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Secrets.Application.Persistence;

public delegate Task RemoveConsumerSecretBindings(
    string tenantId,
    string groupId,
    string resourceType,
    string resourceId,
    CancellationToken ct);

internal sealed class MongoRemoveConsumerSecretBindings(
    IMongoCollection<Secret> secrets,
    MongoRunTransaction transactions,
    StoreSecretChange publish,
    TimeProvider clock)
{
    public async Task InvokeAsync(
        string tenantId,
        string groupId,
        string resourceType,
        string resourceId,
        CancellationToken ct)
    {
        resourceType = ResourceTypeId.Create(resourceType).Value;
        resourceId = SecretIdentity.ObjectId(resourceId);
        var filter = SecretFilters.Consumer(tenantId, groupId, resourceType, resourceId);
        var candidates = await secrets.Find(filter).ToListAsync(ct);
        foreach (var candidate in candidates)
            await transactions.InvokeOutcomeAsync<Unit>(async (tx, token) =>
            {
                var current = await secrets
                    .Find(tx.Session, SecretFilters.Identity(tenantId, candidate.Id))
                    .FirstOrDefaultAsync(token);
                if (current is null)
                    return tx.Commit(Unit.Value);

                var removed = current.Bindings
                    .Where(x => x.ConsumerType == resourceType && x.ConsumerId == resourceId)
                    .ToArray();
                if (removed.Length == 0)
                    return tx.Commit(Unit.Value);

                var updated = current;
                foreach (var binding in removed)
                    updated = updated.Detach(binding.Id, clock.GetUtcNow().UtcDateTime);
                await secrets.ReplaceOneAsync(
                    tx.Session,
                    SecretFilters.Identity(tenantId, current.Id),
                    updated,
                    cancellationToken: token);
                foreach (var binding in removed)
                    await publish(tx.Session, updated, new SecretBindingRemoved(binding.Id), token);
                return tx.Commit(Unit.Value);
            }, ct);
    }
}
