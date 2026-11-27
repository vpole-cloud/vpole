// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Tokens;

public delegate Task PersistIssuedWebServiceDeploymentToken(
    WebServiceDeploymentToken token,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistIssuedWebServiceDeploymentToken(
    IMongoCollection<WebServiceDeploymentToken> tokens
)
{
    public Task InvokeAsync(WebServiceDeploymentToken token, CancellationToken cancellationToken = default) =>
        tokens.InsertOneAsync(token, cancellationToken: cancellationToken);
}
