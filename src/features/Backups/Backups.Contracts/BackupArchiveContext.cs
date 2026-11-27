// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Backups.Contracts;

public sealed record BackupSourceIdentity(
    string TenantId,
    string ResourceType,
    string ResourceId
);

public sealed record BackupServiceSnapshot(
    string Version,
    int StorageGiB,
    int Instances,
    long Revision
);

public sealed record BackupSourcePlacement(
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string NamespaceName,
    string DeploymentId,
    string StorageProfileId,
    long StorageProfileRevision,
    string ComputeProfileId,
    long ComputeProfileRevision
);

public sealed record BackupObjectStorageLocation(
    string Endpoint,
    string Bucket,
    string Prefix
);

public sealed record BackupArchiveLocation(
    string ArchiveId,
    string ServerName,
    BackupObjectStorageLocation Storage
);

public sealed record BackupArchiveContext(
    BackupArchiveLocation Location,
    BackupSourceIdentity Source,
    BackupServiceSnapshot Service,
    BackupSourcePlacement Placement
);

public sealed record BackupCredentialProjection(
    string KubernetesName,
    string AccessKeyIdKey,
    string SecretAccessKeyKey
);
