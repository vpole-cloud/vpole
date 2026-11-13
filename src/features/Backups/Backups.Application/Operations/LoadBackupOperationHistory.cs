using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Operations;

public sealed record BackupOperationHistoryQuery(
    string TenantId,
    BackupOperationCursor? After,
    int PageSize
);

public sealed record BackupOperationHistoryPage(
    BackupOperation[] Items,
    string? NextCursor
);

public sealed class LoadBackupOperationHistory(
    IMongoCollection<BackupOperation> operations
)
{
    public async Task<BackupOperationHistoryPage> LoadAsync(BackupOperationHistoryQuery query, CancellationToken ct)
    {
        var page = await operations.Find(BackupHistoryFilters.Page(query))
            .SortByDescending(operation => operation.CreatedAt)
            .ThenByDescending(operation => operation.Id)
            .Limit(query.PageSize + 1)
            .ToListAsync(ct);
        var items = page.Take(query.PageSize).ToArray();
        var next = page.Count > query.PageSize
            ? new BackupOperationCursor(items[^1].CreatedAt, items[^1].Id).Encode()
            : null;
        return new BackupOperationHistoryPage(items, next);
    }
}
