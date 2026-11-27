// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Tokens;

public delegate Task<WebServiceDeploymentToken?> LoadWebServiceDeploymentTokenByHash(
    string tokenHash,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadWebServiceDeploymentTokenByHash(
    IMongoCollection<WebServiceDeploymentToken> tokens
)
{
    public async Task<WebServiceDeploymentToken?> InvokeAsync(
        string tokenHash,
        CancellationToken cancellationToken = default) => await tokens
        .Find(WebServiceDeploymentTokenFilters.ByTokenHash(tokenHash))
        .FirstOrDefaultAsync(cancellationToken);
}
