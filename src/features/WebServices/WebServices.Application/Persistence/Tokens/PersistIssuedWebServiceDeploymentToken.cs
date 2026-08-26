using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

public delegate Task PersistIssuedWebServiceDeploymentToken(
    WebServiceDeploymentToken token,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistIssuedWebServiceDeploymentToken(
    IMongoCollection<WebServiceDeploymentToken> tokens)
{
    public Task InvokeAsync(
        WebServiceDeploymentToken token,
        CancellationToken cancellationToken = default) =>
        tokens.InsertOneAsync(token, cancellationToken: cancellationToken);
}
