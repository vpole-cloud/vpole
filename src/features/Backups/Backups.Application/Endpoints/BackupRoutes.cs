// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Routing;
using Pole.Features.Backups.Application.Endpoints.Archives;
using Pole.Features.Backups.Application.Endpoints.Destinations;
using Pole.Features.Backups.Application.Endpoints.Operations;
using Pole.Features.Backups.Application.Endpoints.Policies;

namespace Pole.Features.Backups.Application.Endpoints;

internal static class BackupRoutes
{
    internal static void MapBackupRoutes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGetBackupDestination();
        endpoints.MapGetBackupPolicy();
        endpoints.MapGetBackupOperation();
        endpoints.MapCreateBackupDestination();
        endpoints.MapUpdateBackupDestination();
        endpoints.MapDeleteBackupDestination();
        endpoints.MapCreateBackupPolicy();
        endpoints.MapUpdateBackupPolicy();
        endpoints.MapDeleteBackupPolicy();
        endpoints.MapListBackupDestinations();
        endpoints.MapListBackupPolicies();
        endpoints.MapListBackupArchives();
        endpoints.MapListBackupOperations();
        endpoints.MapListBackupCatalog();
        endpoints.MapRunBackup();
        endpoints.MapRestoreBackup();
        endpoints.MapForgetBackupArchive();
    }
}
