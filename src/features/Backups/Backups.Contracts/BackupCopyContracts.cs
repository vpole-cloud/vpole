namespace Pole.Features.Backups.Contracts;

public sealed record BackupCopyRequest(
    string OperationId,
    BackupArchiveContext Archive,
    BackupCredentialProjection Projection
);
