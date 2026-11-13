namespace Pole.Features.Backups.Contracts;

/// <summary>Providers may be called repeatedly after an interrupted dispatch. The operation ID identifies a single durable effect.</summary>
public interface IBackupProvider
{
    string ResourceType { get; }
    string DeclarationKind { get; }
    bool SupportsPointInTimeRecovery { get; }
    ValidateBackupScheduleOutcome ValidateSchedule(BackupSchedule schedule);
    Task<DescribeBackupSourceOutcome> DescribeAsync(BackupSourceIdentity source, CancellationToken ct);

    Task<BackupProviderResult<BackupProviderProgress>> ApplyScheduleAsync(
        ApplyBackupScheduleRequest request,
        CancellationToken ct);

    Task<BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>> ListAsync(
        ListBackupCatalogRequest request,
        CancellationToken ct);

    Task<BackupProviderResult<BackupRuntimeObservation>> ObserveAsync(
        BackupArchiveContext archive,
        CancellationToken ct);

    Task<BackupProviderResult<BackupProviderProgress>> RunAsync(BackupCopyRequest request, CancellationToken ct);
    Task<BackupProviderResult<BackupProviderProgress>> RestoreAsync(RestoreBackupRequest request, CancellationToken ct);
}
