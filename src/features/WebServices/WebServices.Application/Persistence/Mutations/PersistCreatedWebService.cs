using MongoDB.Driver;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Persistence.Mutations;

public delegate Task PersistCreatedWebService(
    IClientSessionHandle session,
    WebService webService,
    WebServiceCreatedEvent createdEvent,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedWebService(
    IMongoCollection<WebService> webServices,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task InvokeAsync(
        IClientSessionHandle session,
        WebService webService,
        WebServiceCreatedEvent createdEvent,
        CancellationToken cancellationToken = default)
    {
        WebServicePersistenceGuards.EnsureTransaction(session, "Web service creation");
        await webServices.InsertOneAsync(
            session,
            webService,
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(createdEvent)],
            cancellationToken);
    }
}
