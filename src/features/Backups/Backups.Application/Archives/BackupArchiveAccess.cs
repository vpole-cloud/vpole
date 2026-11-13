using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Backups.Application.Archives;

public sealed record BackupArchiveProjectionUnavailable;

public union ResolveBackupArchiveProjectionOutcome(BackupCredentialProjection, BackupArchiveProjectionUnavailable);

public sealed class BackupArchiveAccess(
    ReadConsumerSecretValues read,
    EnsureConsumerSecretProjections projections
)
{
    public async Task<BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>> ListAsync(
        BackupArchive archive,
        IBackupProvider provider,
        CancellationToken ct)
    {
        var s = archive.Storage;
        var access = await read(
            new(
                archive.TenantId,
                archive.Placement.ResourceGroupId,
                archive.Placement.IsolationScopeId,
                BackupResourceTypes.Destination,
                archive.DestinationId,
                s.CredentialSecretId,
                [s.AccessKeyIdKey, s.SecretAccessKeyKey]),
            ct);
        if (access is not ConsumerSecretValues credentials)
            return new BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure(
                "catalog-credential-unavailable",
                false);
        return await provider.ListAsync(
            new(Location(archive), new(credentials.Values[s.AccessKeyIdKey], credentials.Values[s.SecretAccessKeyKey])),
            ct);
    }

    public async Task<ResolveBackupArchiveProjectionOutcome> RuntimeAsync(BackupArchive archive, CancellationToken ct)
    {
        var s = archive.Storage;
        var result = await projections(
            new(
                archive.TenantId,
                BackupResourceTypes.Destination,
                archive.DestinationId,
                archive.Placement.CredentialDeploymentId,
                [new(s.CredentialSecretId, s.AccessKeyIdKey), new(s.CredentialSecretId, s.SecretAccessKeyKey)]),
            ct);
        if (result is not ConsumerSecretProjectionKeys keys)
            return new BackupArchiveProjectionUnavailable();
        var id = keys.Keys.Single(k => k.SourceKey == s.AccessKeyIdKey);
        var key = keys.Keys.Single(k => k.SourceKey == s.SecretAccessKeyKey);
        if (id.KubernetesName != key.KubernetesName)
            return new BackupArchiveProjectionUnavailable();
        return new BackupCredentialProjection(id.KubernetesName, id.ProjectedKey, key.ProjectedKey);
    }

    internal static BackupArchiveLocation Location(BackupArchive archive) => new(
        archive.Id,
        archive.ServerName,
        new BackupObjectStorageLocation(archive.Storage.Endpoint, archive.Storage.Bucket, archive.Storage.Prefix));

    internal static BackupArchiveContext Context(BackupArchive archive)
    {
        var placement = archive.Placement;
        return new(
            Location(archive),
            new(archive.TenantId, archive.ResourceType, archive.ResourceId),
            new(archive.ServiceVersion, archive.StorageGiB, archive.Instances, archive.SourceRevision),
            new(
                placement.ResourceGroupId,
                placement.IsolationScopeId,
                placement.DeploymentTargetId,
                placement.RuntimeCellId,
                placement.NamespaceName,
                placement.SourceDeploymentId,
                placement.StorageProfileId,
                placement.StorageProfileRevision,
                placement.ComputeProfileId,
                placement.ComputeProfileRevision));
    }
}
