using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Managed;

internal sealed class MongoReleaseManagedSecret(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretVersion> versions,
    IResourceRegistrar registrar,
    StoreSecretChange publish,
    TimeProvider clock
)
{
    public async Task<ManagedSecretMutationOutcome> InvokeAsync(
        ReleaseManagedSecretRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Managed Secret release requires the owner transaction.");
        var current = await secrets.Find(session, ManagedSecretFilters.Owner(request.TenantId, request.Owner))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new ManagedSecretMutationSucceeded();
        var management = (ProviderManagedSecret)current.Management;
        var released = current.Detach(management.BindingId, clock.GetUtcNow().UtcDateTime);
        if (current.AllowsSharing && !released.Bindings.IsEmpty)
        {
            await secrets.ReplaceOneAsync(
                session,
                SecretFilters.Identity(request.TenantId, current.Id),
                released,
                cancellationToken: ct);
            await publish(session, released, new SecretBindingRemoved(management.BindingId), ct);
        }
        else
        {
            if (await registrar.UnregisterAsync(
                    new UnregisterResourceRequest(
                        TenantId: request.TenantId,
                        ResourceGroupId: current.ResourceGroupId,
                        ResourceTypeId: SecretResource.Type,
                        ResourceId: current.Id),
                    session: session,
                    cancellationToken: ct) is ResourceRegistrationMutationError)
                return new ManagedSecretReferenceRejected();
            await secrets.DeleteOneAsync(
                session,
                SecretFilters.Identity(request.TenantId, current.Id),
                cancellationToken: ct);
            await versions.DeleteManyAsync(
                session,
                SecretFilters.Versions(request.TenantId, current.Id),
                cancellationToken: ct);
            await publish(session, released, new SecretDeletedChange(), ct);
        }

        return new ManagedSecretMutationSucceeded();
    }
}
