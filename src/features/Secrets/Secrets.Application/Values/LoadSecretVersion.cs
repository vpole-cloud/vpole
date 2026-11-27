// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Values;

public delegate Task<SecretVersion?> LoadSecretVersion(
    string tenantId,
    string secretId,
    long version,
    CancellationToken ct);

internal sealed class MongoLoadSecretVersion(
    IMongoCollection<SecretVersion> versions
)
{
    public Task<SecretVersion?> InvokeAsync(
        string tenantId,
        string secretId,
        long version,
        CancellationToken ct) => versions.Find(SecretFilters.Version(tenantId, secretId, version))
        .FirstOrDefaultAsync(ct)!;
}
