using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Persistence;

public delegate Task<DeleteStoredSecretResult> DeleteStoredSecret(
    string tenantId,
    string id,
    CancellationToken ct);

public union DeleteStoredSecretResult(
    Unit,
    SecretMissing,
    SecretBindingsRemain,
    SecretReferenceRejected);

internal sealed class MongoDeleteStoredSecret(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    MongoRunTransaction transactions,
    IResourceRegistrar registrar,
    StoreSecretChange publish)
{
    public Task<DeleteStoredSecretResult> InvokeAsync(
        string tenantId,
        string id,
        CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<DeleteStoredSecretResult>(async (tx, token) =>
        {
            var current = await secrets
                .Find(tx.Session, SecretFilters.Identity(tenantId, id))
                .FirstOrDefaultAsync(token);
            if (current is null)
                return tx.Abort(new SecretMissing());
            if (current.Bindings.Length != 0)
                return tx.Abort(new SecretBindingsRemain());

            var unregistered = await registrar.UnregisterAsync(
                current.TenantId,
                current.ResourceGroupId,
                SecretResource.Type,
                current.Id,
                tx.Session,
                token);
            if (unregistered is ResourceRegistrationMutationError)
                return tx.Abort(new SecretReferenceRejected());
            await secrets.DeleteOneAsync(
                tx.Session,
                SecretFilters.Identity(tenantId, id),
                cancellationToken: token);
            await versions.DeleteManyAsync(
                tx.Session,
                SecretFilters.Versions(tenantId, id),
                cancellationToken: token);
            await publish(tx.Session, current with
            {
                Revision = current.Revision + 1
            }, new SecretDeletedChange(), token);
            return tx.Commit(Unit.Value);
        }, ct);
}
