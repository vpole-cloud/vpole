using Pole.ASP.Helpers;
using Pole.Features.Backups.Application.Endpoints.Operations;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Contracts;

namespace Pole.Features.Backups.Application.Common;

public static class BackupOperationHttpMappings
{
    public static HttpProblem MapOperationMissing(BackupOperationMissing _) => Problem(
        404,
        "backups.not-found",
        "Backup operation not found");

    public static HttpProblem MapCommandIdempotencyConflict(BackupCommandIdempotencyConflict _) => Problem(
        409,
        "backups.idempotency-conflict",
        "Idempotency key belongs to a different backup command");

    public static HttpProblem MapPolicyChangedDuringBackupRequest(BackupPolicyChangedDuringBackupRequest _) => Problem(
        409,
        "backups.policy-changed",
        "Backup policy changed during the request");

    public static HttpProblem MapRestoreStorageTooSmall(BackupRestoreStorageTooSmall _) => Problem(
        422,
        "backups.insufficient-restore-storage",
        "Restore storage is smaller than the source data volume");

    public static HttpProblem MapRestoreCopyUnavailable(BackupRestoreCopyUnavailable _) => Problem(
        422,
        "backups.target-unavailable",
        "Selected backup copy is unavailable");

    public static HttpProblem MapRestoreTimeUnavailable(BackupRestoreTimeUnavailable _) => Problem(
        422,
        "backups.target-unavailable",
        "Recovery time has no suitable completed base backup");

    public static HttpProblem MapRestoreTimeUnsupported(BackupRestoreTimeUnsupported _) => Problem(
        422,
        "backups.unsupported-provider",
        "Time recovery is not supported for this service");

    public static HttpProblem MapRestoreSelectionInvalid(BackupRestoreSelectionInvalid _) => Problem(
        400,
        "backups.invalid-target-time",
        "Select one backup copy or an offset recovery time");

    private static HttpProblem Problem(
        int status,
        string code,
        string title) => new(status, PoleProblemTypeId.FromCode(code), title, code);
}
