// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitProviders.Application.Common;

public sealed record GitHubIntegrationNotConfigured;

public sealed record GitProviderConnectionNotFound(
    string ConnectionId
);

public sealed record GitProviderConnectionHasSourceRepositories(
    string ConnectionId
);

public sealed record SourceRepositoryNotFound(
    string SourceRepositoryId
);

public sealed record SourceRepositoryAlreadyLinked(
    string ConnectionId,
    string RepositoryId,
    string Branch
);

public sealed record SourceRepositoryHasBuildConfiguration(
    string SourceRepositoryId
);

public sealed record RepositorySourceSnapshotTooLarge;

public sealed record SourceRepositoryNameConflict(
    string TechnicalName
);

public sealed record SourceRepositoryGitOwned(
    string SourceRepositoryId
);
