// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Backups.Application.Policies;

public sealed class BackupPolicyQueries(
    IMongoCollection<BackupPolicy> policies,
    IGitOpsMutations git,
    TimeProvider clock
)
{
    public async Task<BackupPolicyDto?> PolicyAsync(
        string tenant,
        string id,
        CancellationToken ct)
    {
        var row = await policies.Find(BackupsFilters<BackupPolicy>.ById(tenant, id)).SingleOrDefaultAsync(ct);
        if (row is null)
            return null;
        return BackupViews.From(row, row.ReportSchedule(clock.GetUtcNow())) with
        {
            GitOps = await git.StatusAsync(new(tenant, BackupDeclarationKinds.Policy, id), ct)
        };
    }

    public async Task<BackupPolicyDto[]> PoliciesAsync(string tenant, CancellationToken ct)
    {
        var rows = await policies.Find(BackupsFilters<BackupPolicy>.Tenant(tenant)).SortBy(p => p.Name).ToListAsync(ct);
        var ownership = await git.StatusesAsync(
            new(tenant, BackupDeclarationKinds.Policy, rows.Select(p => p.Id).ToArray()),
            ct);
        var now = clock.GetUtcNow();
        return rows.Select(p =>
                BackupViews.From(p, p.ReportSchedule(now)) with { GitOps = ownership.GetValueOrDefault(p.Id) })
            .ToArray();
    }
}
