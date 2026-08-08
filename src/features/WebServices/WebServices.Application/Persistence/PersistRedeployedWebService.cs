using MongoDB.Driver;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence;

public delegate Task<Result> PersistRedeployedWebService(
    IClientSessionHandle session,
    WebService current,
    WebService replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistRedeployedWebService(
    IMongoCollection<WebService> webServices)
{
    public async Task<Result> InvokeAsync(
        IClientSessionHandle session,
        WebService current,
        WebService replacement,
        CancellationToken cancellationToken = default)
    {
        WebServicePersistenceGuards.EnsureTransaction(session, "Web service redeployment");
        WebServicePersistenceGuards.EnsureSameIdentity(current, replacement);
        var result = await webServices.ReplaceOneAsync(
            session,
            WebServiceFilters.AtRevision(current),
            replacement,
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1
            ? Result.Success()
            : Failures.Conflict(
                "The web service changed while redeployment was being accepted.");
    }
}
