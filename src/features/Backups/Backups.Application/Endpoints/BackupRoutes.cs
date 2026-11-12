using Microsoft.AspNetCore.Routing;

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
