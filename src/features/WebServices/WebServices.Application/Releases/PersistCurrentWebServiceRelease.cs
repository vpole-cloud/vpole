using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application;
using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Releases;

public delegate Task PersistCurrentWebServiceRelease(
    IClientSessionHandle session,
    WebServiceRelease release,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCurrentWebServiceRelease(
    IMongoCollection<WebServiceRelease> releases)
{
    public async Task InvokeAsync(
        IClientSessionHandle session,
        WebServiceRelease release,
        CancellationToken cancellationToken = default)
    {
        WebServicePersistenceGuards.EnsureTransaction(session, "Web service release persistence");
        await releases.UpdateManyAsync(
            session,
            WebServiceReleaseFilters.PendingForWebService(release.TenantId, release.WebServiceId),
            Builders<WebServiceRelease>.Update
                .Set(item => item.Outcome, WebServiceReleaseOutcome.Superseded)
                .Set(item => item.CompletedAtUtc, release.RequestedAtUtc)
                .Set(item => item.FailureMessage, null),
            cancellationToken: cancellationToken);
        await releases.InsertOneAsync(
            session,
            release,
            cancellationToken: cancellationToken);
    }
}
