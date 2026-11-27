// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Tokens;

public sealed record WebServiceDeploymentTokenNotFound(
    string TokenId
);

public sealed record WebServiceDeploymentTokenRevoked;

public union RevokeWebServiceDeploymentTokenResult(WebServiceDeploymentTokenRevoked, WebServiceDeploymentTokenNotFound);

public delegate Task<RevokeWebServiceDeploymentTokenResult> RevokeWebServiceDeploymentTokenOutcome(
    string tenantId,
    string webServiceId,
    string tokenId,
    string? revokedByUserId,
    DateTime revokedAtUtc,
    CancellationToken cancellationToken = default);

internal sealed class MongoRevokeWebServiceDeploymentToken(
    IMongoCollection<WebServiceDeploymentToken> tokens
)
{
    public async Task<RevokeWebServiceDeploymentTokenResult> InvokeOutcomeAsync(
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
            ? new WebServiceDeploymentTokenNotFound(tokenId)
            : new WebServiceDeploymentTokenRevoked();
    }
}
