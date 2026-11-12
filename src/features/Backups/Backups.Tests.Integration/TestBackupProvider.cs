using Pole.Features.Backups.Contracts;

namespace Pole.Features.Backups.Tests.Integration;

internal sealed class TestBackupProvider : IBackupProvider
{
    public string ResourceType => "pole/test-backup-database";
    public string DeclarationKind => "TestDatabase";
    public bool SupportsPointInTimeRecovery => true;
    public string? ValidateSchedule(BackupSchedule schedule) => schedule.RetentionDays > 0 ? null : "invalid-retention";
    public IReadOnlyList<BackupCatalogEntry> Catalog { get; set; } = [];
    public BackupServiceSnapshot Service { get; set; } = new("17", 4, 1, 1);
    public int? RestoreStorageGiB { get; private set; }
    public Func<RestoreBackupRequest, CancellationToken, Task<BackupProviderPhase>>? CreateTarget { get; set; }
    public bool CatalogUnavailable { get; set; }

    public Task<BackupProviderResult<BackupServiceSnapshot>> DescribeAsync(
        BackupSourceIdentity source,
        CancellationToken ct) => Success(Service);

    public Task<BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>>
        ListAsync(ListBackupCatalogRequest request, CancellationToken ct) => CatalogUnavailable
        ? Task.FromResult<BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>>(
            new BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure("catalog-unavailable", true))
        : Success(Catalog);

    public Task<BackupProviderResult<BackupRuntimeObservation>> ObserveAsync(
        BackupArchiveContext archive,
        CancellationToken ct) => Success(
        new BackupRuntimeObservation(
            "Healthy",
            null,
            DateTimeOffset.UtcNow,
            null,
            [
                new(
                    "native-id",
                    "before",
                    BackupObservedRunState.Succeeded,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow,
                    null),
                new("running-uid", null, BackupObservedRunState.Running, DateTimeOffset.UtcNow, null, null)
            ]));

    public Task<BackupProviderResult<BackupProviderProgress>> ApplyScheduleAsync(
        ApplyBackupScheduleRequest request,
        CancellationToken ct) => Success(new BackupProviderProgress(BackupProviderPhase.Succeeded, null));

    public Task<BackupProviderResult<BackupProviderProgress>>
        RunAsync(BackupCopyRequest request, CancellationToken ct) => Success(
        new BackupProviderProgress(BackupProviderPhase.Succeeded, request.OperationId));

    public async Task<BackupProviderResult<BackupProviderProgress>> RestoreAsync(
        RestoreBackupRequest request,
        CancellationToken ct)
    {
        RestoreStorageGiB = request.StorageGiB;
        var phase = CreateTarget is null ? BackupProviderPhase.Succeeded : await CreateTarget(request, ct);
        return new BackupProviderResult<BackupProviderProgress>.Success(new(phase, null));
    }

    private static Task<BackupProviderResult<T>> Success<T>(T result) =>
        Task.FromResult<BackupProviderResult<T>>(new BackupProviderResult<T>.Success(result));
}
