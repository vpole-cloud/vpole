using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence.Mutations;

internal sealed class MongoAdmitSourceBuildWebService(
    IMongoCollection<WebService> webServices)
{
    public async Task<AdmitSourceBuildWebServiceOutcome> InvokeAsync(
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
            ? new SourceBuildWebServiceAdmitted()
            : new SourceBuildWebServiceNotFound(webServiceId);
    }
}
