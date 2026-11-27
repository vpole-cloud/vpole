// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Managed;

public delegate Task<Secret?> LoadManagedSecret(
    string tenantId,
    ManagedSecretOwner owner,
    CancellationToken ct);

internal sealed class MongoLoadManagedSecret(
    IMongoCollection<Secret> secrets
)
{
    public Task<Secret?> InvokeAsync(
        string tenantId,
        ManagedSecretOwner owner,
        CancellationToken ct) => secrets.Find(ManagedSecretFilters.Owner(tenantId, owner)).FirstOrDefaultAsync(ct)!;
}
