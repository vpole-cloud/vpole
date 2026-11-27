// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Backups.Application.Operations;

internal static class BackupOperationKeys
{
    internal static string Command(string idempotencyKey) => "command:" + idempotencyKey;

    internal static string Scheduled(string archiveId, string nativeUid) => $"scheduled:{archiveId}:{nativeUid}";
}
