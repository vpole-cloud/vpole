// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitProviders.Contracts;

public sealed record SourceRepositoryBuildSnapshot(
    string Id,
    string TechnicalName,
    string ConnectionId,
    string ExternalRepositoryId,
    string Branch,
    long Revision
);

public sealed record SourceRepositoryBuildNotFound(
    string SourceRepositoryId
);

public sealed record SourceRepositoryBuildConnectionNotFound(
    string SourceRepositoryId,
    string ConnectionId
);

public sealed record GitProviderUnsupported(
    string Provider
);

public sealed record GitProviderUnavailable(
    string Provider
);

public union GitProviderRepositoryAccessError(GitProviderUnsupported, GitProviderUnavailable);

public union DownloadSourceRepositoryArchiveError(
    SourceRepositoryBuildNotFound,
    SourceRepositoryBuildConnectionNotFound,
    GitProviderRepositoryAccessError);

public union FindSourceRepositoryBuildOutcome(SourceRepositoryBuildSnapshot, SourceRepositoryBuildNotFound);

public delegate Task<FindSourceRepositoryBuildOutcome> FindSourceRepositoryBuild(
    string tenantId,
    string sourceRepositoryId,
    CancellationToken cancellationToken = default);

/// <summary>
///     Writes the exact requested commit to the caller-owned destination stream.
///     The destination remains owned by the caller and is not disposed by the provider.
/// </summary>
public sealed record SourceRepositoryArchiveDownloaded;

public union DownloadSourceRepositoryArchiveOutcome(
    SourceRepositoryArchiveDownloaded,
    DownloadSourceRepositoryArchiveError);

public delegate Task<DownloadSourceRepositoryArchiveOutcome> DownloadSourceRepositoryArchive(
    DownloadSourceRepositoryArchiveRequest request,
    CancellationToken cancellationToken = default);

public sealed record DownloadSourceRepositoryArchiveRequest(
    string TenantId,
    string SourceRepositoryId,
    string CommitSha,
    Stream Destination
);
