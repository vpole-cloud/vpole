using Pole.Multitenancy.Primitives;

namespace Pole.Features.Backups.Domain;

public interface IBackupDocument : ITenantOwned
{
    string Id { get; }
}

public interface IBackupConfigurationDocument : IBackupDocument
{
    string Name { get; }
    long Revision { get; }
}

public sealed record BackupStorage(
    string Endpoint,
    string Bucket,
    string Prefix,
    string CredentialSecretId,
    string AccessKeyIdKey,
    string SecretAccessKeyKey
);

public sealed record BackupDestination(
    string Id,
    string TenantId,
    string Name,
    string ResourceGroupId,
    string IsolationScopeId,
    BackupStorage Storage,
    long Revision,
    long ReferenceFence
) : IBackupConfigurationDocument
{
    public static BackupDestination Create(
        string id,
        string tenantId,
        string name,
        string resourceGroupId,
        string isolationScopeId,
        BackupStorage storage) => new(id, tenantId, name, resourceGroupId, isolationScopeId, storage, 1, 0);
}

public sealed record ArchivePlacement(
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string NamespaceName,
    string SourceDeploymentId,
    string CredentialDeploymentId,
    string StorageProfileId,
    long StorageProfileRevision,
    string ComputeProfileId,
    long ComputeProfileRevision
);

public sealed record BackupArchive(
    string Id,
    string TenantId,
    string PolicyId,
    string DestinationId,
    string ResourceType,
    string ResourceId,
    string ResourceName,
    long SourceRevision,
    string ServiceVersion,
    int StorageGiB,
    int Instances,
    BackupStorage Storage,
    ArchivePlacement Placement,
    string ServerName,
    bool Active,
    DateTime CreatedAt,
    long ReferenceFence,
    BackupArchiveHistory History
) : IBackupDocument
{
    public static BackupArchive Create(
        string id,
        string tenantId,
        string policyId,
        string destinationId,
        BackupArchiveSource source,
        BackupStorage storage,
        ArchivePlacement placement,
        BackupHistoryStart history,
        DateTime utcNow) => new(
        id,
        tenantId,
        policyId,
        destinationId,
        source.ResourceType,
        source.ResourceId,
        source.Name,
        source.Revision,
        source.Version,
        source.StorageGiB,
        source.Instances,
        storage,
        placement,
        BackupArchiveNames.Server(id),
        true,
        utcNow,
        0,
        BackupArchiveHistory.Begin(history, utcNow));

    public BackupArchive Retire() => this with { Active = false };

    public BackupArchive RetireForReplacement(BackupArchive replacement)
    {
        if (TenantId != replacement.TenantId || ResourceType != replacement.ResourceType ||
            ResourceId != replacement.ResourceId)
            throw new InvalidOperationException("An archive replacement must preserve source ownership.");

        return this with
        {
            Active = false,
            SourceRevision = Math.Max(SourceRevision, replacement.SourceRevision),
            ServiceVersion = replacement.ServiceVersion,
            StorageGiB = Math.Max(StorageGiB, replacement.StorageGiB),
            Instances = replacement.Instances,
        };
    }
}

public sealed record BackupArchiveSource(
    string ResourceType,
    string ResourceId,
    string Name,
    long Revision,
    string Version,
    int StorageGiB,
    int Instances
);

public static class BackupArchiveNames
{
    public static string Server(string archiveId) => $"pole-{archiveId}";
}

public sealed record BackupObservation(
    string WalArchivingStatus,
    DateTime? WalStatusChangedAt,
    DateTime? LastSuccessfulBackupAt,
    DateTime? NextScheduledAt,
    bool NativeScheduleEnabled
);
