using MongoDB.Driver;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence.Lifecycle;

public sealed record WebServiceDeletionAccepted;

public union BeginWebServiceDeletionResult(
    WebServiceDeletionAccepted,
    WebServiceUpdateConflict);

public delegate Task<BeginWebServiceDeletionResult>
    BeginWebServiceDeletionOutcome(
        IClientSessionHandle session,
        WebService current,
        WebService replacement,
        CancellationToken cancellationToken = default);

internal sealed class MongoBeginWebServiceDeletion(
    IMongoCollection<WebService> webServices)
{
    public async Task<BeginWebServiceDeletionResult>
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
            ? new WebServiceDeletionAccepted()
            : new WebServiceUpdateConflict(current.Id);
    }
}
