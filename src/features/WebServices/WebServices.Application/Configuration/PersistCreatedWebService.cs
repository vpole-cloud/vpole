using Pole.Features.WebServices.Application.SourceDelivery;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application;
using Pole.Database;
using MongoDB.Driver;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;

namespace Pole.Features.WebServices.Application.Configuration;

public sealed record WebServiceCreated;
public sealed record WebServiceNameAlreadyExists;
public union PersistCreatedWebServiceResult(
    WebServiceCreated,
    WebServiceNameAlreadyExists);

public delegate Task<PersistCreatedWebServiceResult> PersistCreatedWebService(
    IClientSessionHandle session,
    WebService webService,
    WebServiceCreatedEvent createdEvent,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedWebService(
    IMongoCollection<WebService> webServices,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<PersistCreatedWebServiceResult> InvokeAsync(
        IClientSessionHandle session,
        WebService webService,
        WebServiceCreatedEvent createdEvent,
        CancellationToken cancellationToken = default)
    {
        WebServicePersistenceGuards.EnsureTransaction(session, "Web service creation");
        try
        {
            await webServices.InsertOneAsync(session, webService, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException ex) when (MongoDuplicateKey.IsForIndex(ex, WebServicesIndexes.TechnicalName))
        { return new WebServiceNameAlreadyExists(); }
        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(createdEvent)],
            cancellationToken);
        return new WebServiceCreated();
    }
}
