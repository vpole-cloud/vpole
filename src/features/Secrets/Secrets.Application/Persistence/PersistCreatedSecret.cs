using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Persistence;

public delegate Task<PersistCreatedSecretResult> PersistCreatedSecret(
    string tenantId,
    string groupId,
    string scopeId,
    string name,
    string purpose,
    Dictionary<string, string> values,
    CancellationToken ct);

public union PersistCreatedSecretResult(
    Secret,
    SecretNameAlreadyExists,
    SecretReferenceRejected,
    SecretEncryptionFailed);

internal sealed class MongoPersistCreatedSecret(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    MongoRunTransaction transactions,
    EncryptSecretVersion encrypt,
    AdmitResourceScopeReference admitScope,
    IResourceRegistrar registrar,
    StoreSecretChange publish,
    TimeProvider clock)
{
    public async Task<PersistCreatedSecretResult> InvokeAsync(
        string tenantId,
        string groupId,
        string scopeId,
        string name,
        string purpose,
        Dictionary<string, string> values,
        CancellationToken ct)
    {
        tenantId = SecretIdentity.ObjectId(tenantId);
        groupId = SecretIdentity.ObjectId(groupId);
        scopeId = SecretIdentity.ObjectId(scopeId);
        var now = clock.GetUtcNow().UtcDateTime;
        var secret = SecretFactory.Create(
            ObjectId.GenerateNewId().ToString(),
            tenantId,
            groupId,
            scopeId,
            name,
            purpose,
            values.Keys,
            now);
        var encrypted = await encrypt(secret, values, ct);
        if (encrypted is not SecretVersion version)
            return new SecretEncryptionFailed();
        return await transactions.InvokeOutcomeAsync<PersistCreatedSecretResult>(async (tx, token) =>
        {
            if (await admitScope(
                new AdmitResourceScopeReferenceRequest(
                    TenantId: tenantId,
                    ResourceGroupId: groupId,
                    IsolationScopeId: scopeId,
                    Consumer: null),
                session: tx.Session,
                cancellationToken: token) is ResourceScopeReferenceUnavailable)
                return tx.Abort(new SecretReferenceRejected());

            var registration = await registrar.RegisterAsync(
                new RegisterResourceRequest(
                    TenantId: tenantId,
                    ResourceGroupId: groupId,
                    Resource: new(SecretResource.Type, secret.Id, secret.Name)),
                session: tx.Session,
                cancellationToken: token);
            if (registration is ResourceRegistrationMutationError)
                return tx.Abort(new SecretReferenceRejected());
            try
            {
                await secrets.InsertOneAsync(tx.Session, secret, cancellationToken: token);
            }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                return tx.Abort(new SecretNameAlreadyExists());
            }
            await versions.InsertOneAsync(
                tx.Session,
                version,
                cancellationToken: token);
            await publish(tx.Session, secret, new SecretCreated(), token);
            return tx.Commit(secret);
        }, ct);
    }
}
