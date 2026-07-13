using MongoDB.Driver;
using Pole.Database;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Endpoints;

internal static class WebServiceDeploymentTokenAccess
{
    public static Task<bool> WebServiceExistsAsync(
        IWebServiceDbContext dbContext,
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken)
    {
        var filter = Builders<WebService>.Filter.And(
            Builders<WebService>.Filter.ForTenant(tenantId),
            Builders<WebService>.Filter.Eq(service => service.Id, webServiceId));

        return dbContext.WebServices
            .Find(filter)
            .AnyAsync(cancellationToken);
    }
}
