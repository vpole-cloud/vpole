// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitOps.Contracts;

// Server-created bootstrap identifiers. These are never accepted from resource YAML.
public sealed record RepositorySourceReference(
    string TenantId,
    string ConnectionId,
    string ExternalRepositoryId
);

public sealed record ResolveRepositoryCommitRequest(
    RepositorySourceReference Repository,
    string Branch
);

public sealed record ReadRepositorySnapshotRequest(
    RepositorySourceReference Repository,
    string Commit,
    string ManifestDirectory
);

public sealed record RepositoryCommitResolved(
    string Commit
);

public sealed record RepositoryManifestFile(
    string Path,
    string Content
);

// An empty Files list is success only after a complete read of an existing directory.
public sealed record RepositorySnapshotRead(
    string Commit,
    IReadOnlyList<RepositoryManifestFile> Files
);

public sealed record RepositoryAccessGranted(
    string DisplayUrl
);

public union CheckRepositoryAccessOutcome(
    RepositoryAccessGranted,
    RepositoryAccessDenied,
    RepositoryNotFound,
    RepositoryTemporarilyUnavailable);

public sealed record RepositoryAccessDenied;

public sealed record RepositoryNotFound;

public sealed record RepositoryRefNotFound(
    string Ref
);

public sealed record RepositoryDirectoryNotFound(
    string Path
);

public enum RepositorySnapshotRejection
{
    UnsafeArchive,
    SizeLimit,
    InvalidManifestEncoding,
    InvalidPath,
    InvalidCommit,
}

public sealed record RepositorySnapshotRejected(
    RepositorySnapshotRejection Reason
);

public sealed record RepositoryTemporarilyUnavailable;

public union ResolveRepositoryCommitOutcome(
    RepositoryCommitResolved,
    RepositoryAccessDenied,
    RepositoryNotFound,
    RepositoryRefNotFound,
    RepositoryTemporarilyUnavailable);

public union ReadRepositorySnapshotOutcome(
    RepositorySnapshotRead,
    RepositoryAccessDenied,
    RepositoryNotFound,
    RepositoryRefNotFound,
    RepositoryDirectoryNotFound,
    RepositorySnapshotRejected,
    RepositoryTemporarilyUnavailable);

// GitProviders contributes this extension. GitOps does not import its implementation.
public interface IRepositorySnapshotProvider
{
    string Provider { get; }

    Task<CheckRepositoryAccessOutcome> CheckAccessAsync(
        RepositorySourceReference repository,
        CancellationToken cancellationToken);

    Task<ResolveRepositoryCommitOutcome> ResolveCommitAsync(
        ResolveRepositoryCommitRequest request,
        CancellationToken cancellationToken);

    Task<ReadRepositorySnapshotOutcome> ReadSnapshotAsync(
        ReadRepositorySnapshotRequest request,
        CancellationToken cancellationToken);
}
