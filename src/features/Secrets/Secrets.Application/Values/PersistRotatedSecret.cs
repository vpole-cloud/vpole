using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Values;

public delegate Task<PersistRotatedSecretResult> PersistRotatedSecret(
    string tenantId,
    string id,
    long expectedVersion,
    Dictionary<string, string> values,
    CancellationToken ct);

public union PersistRotatedSecretResult(
    Secret,
    SecretMissing,
    SecretVersionConflict,
    SecretMappedKeysRequired,
    SecretEncryptionFailed,
    SecretManagedByProvider);

internal sealed class MongoPersistRotatedSecret(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    MongoRunTransaction transactions,
    EncryptSecretVersion encrypt,
    StoreSecretChange publish,
    TimeProvider clock)
{
    public Task<PersistRotatedSecretResult> InvokeAsync(
        string tenantId,
        string id,
        long expectedVersion,
        Dictionary<string, string> values,
        CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<PersistRotatedSecretResult>(async (tx, token) =>
        {
            var current = await secrets
                .Find(tx.Session, SecretFilters.Identity(tenantId, id))
                .FirstOrDefaultAsync(token);
            if (current is null)
                return tx.Abort(new SecretMissing());
            if (current.Management is ProviderManagedSecret)
                return tx.Abort(new SecretManagedByProvider());
            if (current.CurrentVersion != expectedVersion)
                return tx.Abort(new SecretVersionConflict());
            if (current.DeclaredKeys && !current.KeyNames.ToHashSet(StringComparer.Ordinal).SetEquals(values.Keys))
                return tx.Abort(new SecretMappedKeysRequired());
            if (current.Bindings.Any(x => x.KeyMappings.Keys.Concat(x.Pins.ProtectedKeys).Except(values.Keys).Any()))
                return tx.Abort(new SecretMappedKeysRequired());

            var rotated = current.Rotate(values.Keys.Order().ToArray(), clock.GetUtcNow().UtcDateTime);
            var encrypted = await encrypt(rotated, values, token);
            if (encrypted is not SecretVersion version)
                return tx.Abort(new SecretEncryptionFailed());
            // Acquire the aggregate write conflict before the immutable version insert. A competing
            // rotation retries its snapshot and returns a version conflict, never a duplicate-key 5xx.
            await secrets.ReplaceOneAsync(
                tx.Session,
                SecretFilters.Identity(tenantId, id),
                rotated,
                cancellationToken: token);
            await versions.InsertOneAsync(
                tx.Session,
                version,
                cancellationToken: token);
            await publish(tx.Session, rotated, new SecretRotated(), token);
            return tx.Commit(rotated);
        }, ct);
}
