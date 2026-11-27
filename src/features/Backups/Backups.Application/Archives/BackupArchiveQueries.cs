// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Archives;

public sealed class BackupArchiveQueries(
    IMongoCollection<BackupArchive> archives,
    BackupProviderCatalog providers
)
{
    public async Task<BackupArchiveDto[]> ArchivesAsync(string tenant, CancellationToken ct) =>
        (await archives.Find(BackupsFilters<BackupArchive>.Tenant(tenant))
            .SortByDescending(a => a.CreatedAt)
            .ToListAsync(ct)).Select(a => BackupViews.From(
            a,
            providers.Find(a.ResourceType)?.SupportsPointInTimeRecovery == true))
        .ToArray();
}
