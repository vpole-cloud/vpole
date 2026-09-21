using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence.Mutations;

internal sealed class MongoAdmitSourceBuildWebService(
    IMongoCollection<WebService> webServices)
{
    public async Task<Result<Unit, SourceBuildWebServiceNotFound>> InvokeAsync(
        string tenantId,
        string webServiceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Source build admission requires a transaction.");

        // A real write (not a read/no-op) conflicts with concurrent deletion. Revision is
        // the persistence fence; ActiveRevision and the desired deployment stay unchanged.
        var result = await webServices.UpdateOneAsync(
            session,
            WebServiceFilters.InLifecycle<ActiveWebServiceLifecycle>(tenantId, webServiceId),
            Builders<WebService>.Update.Inc(item => item.Revision, 1),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1
            ? new Ok<Unit>(Unit.Value)
            : new Err<SourceBuildWebServiceNotFound>(new(webServiceId));
    }
}
