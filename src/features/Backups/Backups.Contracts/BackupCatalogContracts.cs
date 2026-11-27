// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Backups.Contracts;

public sealed record BackupCatalogEntry(
    string BackupId,
    DateTimeOffset CompletedAt
);

public sealed record BackupCatalogCredentials(
    string AccessKeyId,
    string SecretAccessKey
)
{
    public override string ToString() => "BackupCatalogCredentials([REDACTED])";
}

public sealed record ListBackupCatalogRequest(
    BackupArchiveLocation Archive,
    BackupCatalogCredentials Credentials
);

public sealed record BackupCatalogContents(
    IReadOnlyList<BackupCatalogEntry> Entries
);

public sealed record BackupCatalogAccessDenied;

public sealed record BackupCatalogUnavailable;

public sealed record BackupCatalogUnreadable;

public sealed record BackupCatalogTimeout;

public union ReadBackupCatalogOutcome(
    BackupCatalogContents,
    BackupCatalogAccessDenied,
    BackupCatalogUnavailable,
    BackupCatalogUnreadable,
    BackupCatalogTimeout);
