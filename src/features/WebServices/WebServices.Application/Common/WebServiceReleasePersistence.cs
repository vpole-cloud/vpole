using MongoDB.Driver;
using Pole.Database;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

internal static class WebServiceReleasePersistence
{
    public static async Task InsertCurrentAsync(
        IWebServiceDbContext dbContext,
        IClientSessionHandle session,
        WebServiceRelease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(release);

        var olderPendingFilter = Builders<WebServiceRelease>.Filter.And(
            Builders<WebServiceRelease>.Filter.ForTenant(release.TenantId),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.WebServiceId,
                release.WebServiceId),
            Builders<WebServiceRelease>.Filter.Eq(
                item => item.Outcome,
                WebServiceReleaseOutcome.Pending),
            Builders<WebServiceRelease>.Filter.Lt(
                item => item.DeploymentGeneration,
                release.DeploymentGeneration));
        await dbContext.WebServiceReleases.UpdateManyAsync(
            session,
            olderPendingFilter,
            Builders<WebServiceRelease>.Update
                .Set(item => item.Outcome, WebServiceReleaseOutcome.Superseded)
                .Set(item => item.CompletedAtUtc, release.RequestedAtUtc)
                .Set(item => item.FailureMessage, null),
            cancellationToken: cancellationToken);
        await dbContext.WebServiceReleases.InsertOneAsync(
            session,
            release,
            cancellationToken: cancellationToken);
    }
}
