namespace Pole.Features.Backups.Application.Operations;

internal static class BackupOperationKeys
{
    internal static string Command(string idempotencyKey) => "command:" + idempotencyKey;

    internal static string Scheduled(string archiveId, string nativeUid) => $"scheduled:{archiveId}:{nativeUid}";
}
