using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Backups.Application.Execution;

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
                BackupConfigurationWriter.DestinationType,
                archive.DestinationId,
                s.CredentialSecretId,
                [s.AccessKeyIdKey, s.SecretAccessKeyKey]),
            ct);
        if (access is not ConsumerSecretValues credentials)
            return new BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure(
                "catalog-credential-unavailable",
                false);
        return await provider.ListAsync(
            new(
                Context(archive, string.Empty, s.AccessKeyIdKey, s.SecretAccessKeyKey),
                new(credentials.Values[s.AccessKeyIdKey], credentials.Values[s.SecretAccessKeyKey])),
            ct);
    }

    public async Task<BackupProviderResult<BackupArchiveContext>> RuntimeAsync(
        BackupArchive archive,
        CancellationToken ct)
    {
        var s = archive.Storage;
        var result = await projections(
            new(
                archive.TenantId,
                BackupConfigurationWriter.DestinationType,
                archive.DestinationId,
                archive.Placement.CredentialDeploymentId,
                [new(s.CredentialSecretId, s.AccessKeyIdKey), new(s.CredentialSecretId, s.SecretAccessKeyKey)]),
            ct);
        if (result is not ConsumerSecretProjectionKeys keys)
            return new BackupProviderResult<BackupArchiveContext>.Failure("workload-credential-unavailable", true);
        var id = keys.Keys.Single(k => k.SourceKey == s.AccessKeyIdKey);
        var key = keys.Keys.Single(k => k.SourceKey == s.SecretAccessKeyKey);
        if (id.KubernetesName != key.KubernetesName)
            return new BackupProviderResult<BackupArchiveContext>.Failure("workload-credential-unavailable", true);
        return new BackupProviderResult<BackupArchiveContext>.Success(
            Context(archive, id.KubernetesName, id.ProjectedKey, key.ProjectedKey));
    }

    internal static BackupArchiveContext Context(
        BackupArchive a,
        string native,
        string id,
        string key)
    {
        var p = a.Placement;
        return new(
            a.Id,
            new(a.TenantId, a.ResourceType, a.ResourceId),
            new(a.ServiceVersion, a.StorageGiB, a.Instances, a.SourceRevision),
            new(
                p.ResourceGroupId,
                p.IsolationScopeId,
                p.DeploymentTargetId,
                p.RuntimeCellId,
                p.NamespaceName,
                p.SourceDeploymentId,
                p.StorageProfileId,
                p.StorageProfileRevision,
                p.ComputeProfileId,
                p.ComputeProfileRevision),
            a.Storage.Endpoint,
            a.Storage.Bucket,
            a.Storage.Prefix,
            a.ServerName,
            native,
            id,
            key);
    }
}
