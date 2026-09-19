using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Persistence;

public delegate Task<RemoveSecretBindingResult> RemoveSecretBinding(
    string tenantId,
    string id,
    string bindingId,
    CancellationToken ct);

public union RemoveSecretBindingResult(
    Secret,
    SecretMissing);

internal sealed class MongoRemoveSecretBinding(
    IMongoCollection<Secret> secrets,
    MongoRunTransaction transactions,
    StoreSecretChange publish,
    TimeProvider clock)
{
    public Task<RemoveSecretBindingResult> InvokeAsync(
        string tenantId,
        string id,
        string bindingId,
        CancellationToken ct)
    {
        bindingId = SecretIdentity.ObjectId(bindingId);
        return transactions.InvokeOutcomeAsync<RemoveSecretBindingResult>(async (tx, token) =>
        {
            var current = await secrets
                .Find(tx.Session, SecretFilters.Identity(tenantId, id))
                .FirstOrDefaultAsync(token);
            if (current is null)
                return tx.Abort(new SecretMissing());
            if (current.Bindings.All(x => x.Id != bindingId))
                return tx.Commit(current);

            var detached = current.Detach(bindingId, clock.GetUtcNow().UtcDateTime);
            await secrets.ReplaceOneAsync(
                tx.Session,
                SecretFilters.Identity(tenantId, id),
                detached,
                cancellationToken: token);
            await publish(tx.Session, detached, new SecretBindingRemoved(bindingId), token);
            return tx.Commit(detached);
        }, ct);
    }
}
