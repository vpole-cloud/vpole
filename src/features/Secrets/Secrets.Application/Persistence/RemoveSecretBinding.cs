using Pole.Features.GitOps.Contracts;
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
    SecretMissing,
    SecretManagedByProvider,
    SecretGitOwned);

internal sealed class MongoRemoveSecretBinding(
    IMongoCollection<Secret> secrets,
    MongoRunTransaction transactions,
    StoreSecretChange publish,
    TimeProvider clock,
    IGitOpsMutations git)
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
            if (!await git.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(
                    TenantId: tenantId,
                    Kind: "SecretBinding",
                    ResourceId: bindingId),
                session: tx.Session,
                ct: token))
                return tx.Abort(new SecretGitOwned());
            if (current.Management is ProviderManagedSecret owner && owner.BindingId == bindingId)
                return tx.Abort(new SecretManagedByProvider());
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
