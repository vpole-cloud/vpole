// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Backups.Application.Destinations;

public sealed class BackupDestinationQueries(
    IMongoCollection<BackupDestination> destinations,
    IGitOpsMutations git
)
{
    public async Task<BackupDestinationDto?> DestinationAsync(
        string tenant,
        string id,
        CancellationToken ct)
    {
        var row = await destinations.Find(BackupsFilters<BackupDestination>.ById(tenant, id)).SingleOrDefaultAsync(ct);
        if (row is null)
            return null;
        return BackupDestinationDto.From(row) with
        {
            GitOps = await git.StatusAsync(new(tenant, BackupDeclarationKinds.Destination, id), ct)
        };
    }

    public async Task<BackupDestinationDto[]> DestinationsAsync(string tenant, CancellationToken ct)
    {
        var rows = await destinations.Find(BackupsFilters<BackupDestination>.Tenant(tenant))
            .SortBy(d => d.Name)
            .ToListAsync(ct);
        var ownership = await git.StatusesAsync(
            new(tenant, BackupDeclarationKinds.Destination, rows.Select(d => d.Id).ToArray()),
            ct);
        return rows.Select(d => BackupDestinationDto.From(d) with { GitOps = ownership.GetValueOrDefault(d.Id) })
            .ToArray();
    }
}
