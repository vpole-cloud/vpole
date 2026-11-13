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
