using MongoDB.Driver;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence;

public delegate Task<Result> BeginWebServiceDeletion(
    IClientSessionHandle session,
    WebService current,
    WebService replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginWebServiceDeletion(
    IMongoCollection<WebService> webServices)
{
    public async Task<Result> InvokeAsync(
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
            ? Result.Success()
            : Failures.Conflict(
                "The web service changed while deletion was being accepted.");
    }
}
