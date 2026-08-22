using MongoDB.Driver;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence;

public sealed record WebServiceDeletionAccepted;

public delegate Task<Result<WebServiceDeletionAccepted, WebServiceUpdateConflict>>
    BeginWebServiceDeletionOutcome(
        IClientSessionHandle session,
        WebService current,
        WebService replacement,
        CancellationToken cancellationToken = default);

internal sealed class MongoBeginWebServiceDeletion(
    IMongoCollection<WebService> webServices)
{
    public async Task<Result<WebServiceDeletionAccepted, WebServiceUpdateConflict>>
        InvokeOutcomeAsync(
            IClientSessionHandle session,
            WebService current,
            WebService replacement,
            CancellationToken cancellationToken = default)
    {
        WebServicePersistenceGuards.EnsureTransaction(session, "Web service deletion start");
        WebServicePersistenceGuards.EnsureSameIdentity(current, replacement);
        var result = await webServices.ReplaceOneAsync(
            session,
            WebServiceFilters.AtRevision(current),
            replacement,
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1
            ? new Ok<WebServiceDeletionAccepted>(new WebServiceDeletionAccepted())
            : new Err<WebServiceUpdateConflict>(
                new WebServiceUpdateConflict(current.Id));
    }
}
