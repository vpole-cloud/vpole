// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Projections;

public sealed record SecretProjectionCandidate(
    string TenantId,
    string SecretId
);

public delegate Task<IReadOnlyList<SecretProjectionCandidate>> LoadSecretProjectionCandidates(
    string? afterSecretId,
    DateTime deletedAuditBefore,
    CancellationToken ct);

internal sealed class MongoLoadSecretProjectionCandidates(
    IMongoCollection<Secret> secrets,
    IMongoCollection<SecretProjection> projections
)
{
    private const int BatchSize = 128;

    public async Task<IReadOnlyList<SecretProjectionCandidate>> InvokeAsync(
        string? afterSecretId,
        DateTime deletedAuditBefore,
        CancellationToken ct)
    {
        // System maintenance enumerates tenants in bounded pages; every operation is tenant-scoped.
        var secretFilter = SecretProjectionFilters.SourceCandidates(afterSecretId);
        var projectionFilter = SecretProjectionFilters.RecoveryCandidates(afterSecretId, deletedAuditBefore);
        var active = await secrets.Find(secretFilter)
            .SortBy(x => x.Id)
            .Limit(BatchSize)
            .Project(x => new SecretProjectionCandidate(x.TenantId, x.Id))
            .ToListAsync(ct);
        var existing = await projections.Aggregate()
            .Match(projectionFilter)
            .Group(
                x => new { x.TenantId, x.SecretId },
                group => new SecretProjectionCandidate(group.Key.TenantId, group.Key.SecretId))
            .SortBy(x => x.SecretId)
            .Limit(BatchSize)
            .ToListAsync(ct);
        return active.Concat(existing)
            .Distinct()
            .OrderBy(x => x.SecretId, StringComparer.Ordinal)
            .Take(BatchSize)
            .ToArray();
    }
}
