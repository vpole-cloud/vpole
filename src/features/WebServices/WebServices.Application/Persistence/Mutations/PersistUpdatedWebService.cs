using MongoDB.Driver;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence.Mutations;

public sealed record WebServiceUpdateAccepted;

public sealed record WebServiceUpdateConflict(string WebServiceId);

public delegate Task<Result<WebServiceUpdateAccepted, WebServiceUpdateConflict>>
    PersistUpdatedWebServiceOutcome(
        IClientSessionHandle session,
        WebService current,
        WebService replacement,
        WebServiceUpdatedEvent updatedEvent,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedWebService(
    IMongoCollection<WebService> webServices,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<Result<WebServiceUpdateAccepted, WebServiceUpdateConflict>>
        InvokeOutcomeAsync(
            IClientSessionHandle session,
            WebService current,
            WebService replacement,
            WebServiceUpdatedEvent updatedEvent,
            CancellationToken cancellationToken = default)
    {
        WebServicePersistenceGuards.EnsureTransaction(session, "Web service update");
        WebServicePersistenceGuards.EnsureSameIdentity(current, replacement);
        if (!string.Equals(
                updatedEvent.WebServiceId,
                current.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                updatedEvent.EventCore.TenantId,
                current.TenantId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A web service update event must preserve tenant and resource identity.");
        }

        var result = await webServices.ReplaceOneAsync(
            session,
            WebServiceFilters.AtRevision(current),
            replacement,
            cancellationToken: cancellationToken);
        if (result.ModifiedCount != 1)
        {
            return new Err<WebServiceUpdateConflict>(
                new WebServiceUpdateConflict(current.Id));
        }

        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(updatedEvent)],
            cancellationToken);
        return new Ok<WebServiceUpdateAccepted>(new WebServiceUpdateAccepted());
    }
}
