using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.ResultType;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class MongoReleaseManagedSecret(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    IResourceRegistrar registrar,
    StoreSecretChange publish,
    TimeProvider clock)
{
    public async Task<Result<Unit, ManagedSecretError>> InvokeAsync(
        string tenantId,
        ManagedSecretOwner owner,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Managed Secret release requires the owner transaction.");
        var current = await secrets.Find(session, ManagedSecretFilters.Owner(tenantId, owner)).FirstOrDefaultAsync(ct);
        if (current is null)
            return new Ok<Unit>(Unit.Value);
        var management = (ProviderManagedSecret)current.Management;
        var released = current.Detach(management.BindingId, clock.GetUtcNow().UtcDateTime);
        if (!released.Bindings.IsEmpty)
        {
            await secrets.ReplaceOneAsync(session, SecretFilters.Identity(tenantId, current.Id), released, cancellationToken: ct);
            await publish(session, released, new SecretBindingRemoved(management.BindingId), ct);
        }
        else
        {
            if (await registrar.UnregisterAsync(tenantId, current.ResourceGroupId, SecretResource.Type,
                    current.Id, session, ct) is Err<ResourceRegistrationMutationError>)
                return new Err<ManagedSecretError>(new ManagedSecretReferenceRejected());
            await secrets.DeleteOneAsync(session, SecretFilters.Identity(tenantId, current.Id), cancellationToken: ct);
            await versions.DeleteManyAsync(session, SecretFilters.Versions(tenantId, current.Id), cancellationToken: ct);
            await publish(session, released, new SecretDeletedChange(), ct);
        }
        return new Ok<Unit>(Unit.Value);
    }
}
