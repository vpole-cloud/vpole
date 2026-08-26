using MongoDB.Driver;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;

namespace Pole.Features.WebServices.Application.Persistence.Tokens;

public sealed record WebServiceDeploymentTokenNotFound(string TokenId);

public delegate Task<Result<Unit, WebServiceDeploymentTokenNotFound>>
    RevokeWebServiceDeploymentTokenOutcome(
        string tenantId,
        string webServiceId,
        string tokenId,
        string? revokedByUserId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default);

internal sealed class MongoRevokeWebServiceDeploymentToken(
    IMongoCollection<WebServiceDeploymentToken> tokens)
{
    public async Task<Result<Unit, WebServiceDeploymentTokenNotFound>> InvokeOutcomeAsync(
        string tenantId,
        string webServiceId,
        string tokenId,
        string? revokedByUserId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var result = await tokens.UpdateOneAsync(
            WebServiceDeploymentTokenFilters.ById(tenantId, webServiceId, tokenId),
            Builders<WebServiceDeploymentToken>.Update
                .Set(item => item.RevokedAtUtc, revokedAtUtc)
                .Set(item => item.RevokedByUserId, revokedByUserId),
            cancellationToken: cancellationToken);
        return result.MatchedCount == 0
            ? new Err<WebServiceDeploymentTokenNotFound>(
                new WebServiceDeploymentTokenNotFound(tokenId))
            : new Ok<Unit>(Unit.Value);
    }
}
