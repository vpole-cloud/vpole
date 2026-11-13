using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Archives;

public sealed record LoadArchiveBackupCatalogRequest(
    string TenantId,
    string ArchiveId
);

public union LoadArchiveBackupCatalogOutcome(
    BackupCatalogContents,
    BackupArchiveMissing,
    BackupSourceUnsupported,
    BackupCatalogCredentialUnavailable,
    BackupCatalogAccessDenied,
    BackupCatalogUnavailable,
    BackupCatalogUnreadable,
    BackupCatalogTimeout);

public sealed class LoadArchiveBackupCatalog(
    IMongoCollection<BackupArchive> archives,
    BackupProviderCatalog providers,
    BackupArchiveAccess access
)
{
    public async Task<LoadArchiveBackupCatalogOutcome> ExecuteAsync(
        LoadArchiveBackupCatalogRequest request,
        CancellationToken ct)
    {
        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(request.TenantId, request.ArchiveId))
            .SingleOrDefaultAsync(ct);
        if (archive is null)
            return new BackupArchiveMissing();
        var provider = providers.Find(archive.ResourceType);
        if (provider is null)
            return new BackupSourceUnsupported();

        var result = await access.ListAsync(archive, provider, ct);
        return result switch
        {
            BackupCatalogContents catalog => new BackupCatalogContents(
                catalog.Entries.OrderByDescending(entry => entry.CompletedAt).ToArray()),
            BackupCatalogCredentialUnavailable error => error,
            BackupCatalogAccessDenied error => error,
            BackupCatalogUnavailable error => error,
            BackupCatalogUnreadable error => error,
            BackupCatalogTimeout error => error,
        };
    }
}
