// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Projections;

public delegate Task<IReadOnlyList<SecretProjection>> LoadSecretProjections(
    string tenantId,
    string secretId,
    CancellationToken ct);

internal sealed class MongoLoadSecretProjections(
    IMongoCollection<SecretProjection> projections
)
{
    public async Task<IReadOnlyList<SecretProjection>> InvokeAsync(
        string tenantId,
        string secretId,
        CancellationToken ct) => await projections
        .Find(SecretProjectionFilters.ForSecret(tenantId, secretId))
        .ToListAsync(ct);
}
