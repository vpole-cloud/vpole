using MongoDB.Driver;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Tokens;

public delegate Task TouchWebServiceDeploymentToken(
    IClientSessionHandle session,
    string tenantId,
    string webServiceId,
    string tokenId,
    DateTime usedAtUtc,
    string? source,
    CancellationToken cancellationToken = default);

internal sealed class MongoTouchWebServiceDeploymentToken(
    IMongoCollection<WebServiceDeploymentToken> tokens)
{
    public async Task InvokeAsync(
        IClientSessionHandle session,
        string tenantId,
        string webServiceId,
        string tokenId,
        DateTime usedAtUtc,
        string? source,
        CancellationToken cancellationToken = default)
    {
        WebServicePersistenceGuards.EnsureTransaction(session, "Deployment token usage update");
        await tokens.UpdateOneAsync(
            session,
            WebServiceDeploymentTokenFilters.ById(
                tenantId,
                webServiceId,
                tokenId),
            Builders<WebServiceDeploymentToken>.Update
                .Set(item => item.LastUsedAtUtc, usedAtUtc)
                .Set(item => item.LastUsedSource, source),
            cancellationToken: cancellationToken);
    }
}
