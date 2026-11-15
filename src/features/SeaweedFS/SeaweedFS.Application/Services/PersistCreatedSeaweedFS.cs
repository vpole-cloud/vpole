using MongoDB.Driver;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Mongo;
using Pole.Features.SeaweedFS.Domain;

namespace Pole.Features.SeaweedFS.Application.Services;

public sealed record SeaweedFSServiceNameAlreadyExists(
    string Name
);

public sealed record SeaweedFSServicePersisted;

public union PersistCreatedSeaweedFSOutcome(SeaweedFSServicePersisted, SeaweedFSServiceNameAlreadyExists);

public delegate Task<PersistCreatedSeaweedFSOutcome> PersistCreatedSeaweedFS(
    IClientSessionHandle session,
    SeaweedFSService service,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedSeaweedFS(
    IMongoCollection<SeaweedFSService> services,
    StoreOutboxWrites storeOutboxWrites
)
{
    public async Task<PersistCreatedSeaweedFSOutcome> InvokeAsync(
        IClientSessionHandle session,
        SeaweedFSService service,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("SeaweedFS creation requires a transaction.");

        try
        {
            await services.InsertOneAsync(session, service, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey &&
                                                    exception.WriteError.Message.Contains(
                                                        "UX_SeaweedFS_Tenant_Name",
                                                        StringComparison.Ordinal))
        {
            return new SeaweedFSServiceNameAlreadyExists(service.Name);
        }

        var created = new Pole.Features.SeaweedFS.Events.SeaweedFSServiceCreatedEvent(
            service.Id,
            service.Name,
            service.ResourceGroupId,
            new(service.Spec.Mode.ToString(), service.Spec.Storage.SizeGb, service.Spec.Storage.StorageProfileId),
            service.Tags,
            service.Revision,
            Pole.Features.Events.Abstractions.EventCore.Create(service.TenantId));
        await storeOutboxWrites(session, [OutboxWrite.Create(created)], cancellationToken);
        return new SeaweedFSServicePersisted();
    }
}
