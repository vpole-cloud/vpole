using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application;
using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Tokens;

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
