using MongoDB.Driver;

namespace Pole.Features.Secrets.Contracts;

public sealed record BackupStorageSecretAccess(
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    string DestinationId,
    string SecretId,
    string AccessKeyIdKey,
    string SecretAccessKeyKey
);

public sealed record BackupStorageCredentials(
    string AccessKeyId,
    string SecretAccessKey
)
{
    public override string ToString() => "BackupStorageCredentials([REDACTED])";
}

public sealed record BackupStorageSecretDenied;

public sealed record BackupStorageSecretGranted;

public union GrantBackupStorageSecretOutcome(BackupStorageSecretGranted, BackupStorageSecretDenied);

public union ReadBackupStorageSecretOutcome(
    BackupStorageCredentials,
    BackupStorageSecretDenied,
    ManagedSecretEncryptionUnavailable);

/// <summary>Establish the purpose-specific grant inside the authorized destination mutation.</summary>
public delegate Task<GrantBackupStorageSecretOutcome> GrantBackupStorageSecret(
    BackupStorageSecretAccess request,
    IClientSessionHandle session,
    CancellationToken ct);

public delegate Task<ReadBackupStorageSecretOutcome> ReadBackupStorageSecret(
    BackupStorageSecretAccess request,
    CancellationToken ct);
